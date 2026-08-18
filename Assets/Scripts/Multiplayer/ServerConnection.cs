namespace Assets.Scripts.Multiplayer
{
    using Assets.Scripts.Clock;
    using Assets.Scripts.Flight;
    using Assets.Scripts.Flight.Sim;
    using Multiplayer.CraftData;
    using System;
    using System.Collections.Generic;
    using System.Net;
    using System.Net.Sockets;
    using System.Threading.Tasks;

    public class ClientSession
    {
        public int Id { get; set; }
        public TcpClient Client { get; set; }
        public string CraftXml { get; set; }
        public bool IsHost => Client == null;
    }

    public static class ServerConnection
    {
        public const int HostClientId = 0;
        private static TcpListener _listener;
        private static bool _isHosting;

        public static List<ClientSession> Sessions { get; } = new List<ClientSession>();
        private static int _nextClientId = HostClientId;

        // Server time stuff
        private const int ServerTickRate = 60;
        private const int MaximumCatchUpTicksPerFrame = 4;
        private static ServerClock _serverClock;

        // Subscribe telemetry, physics mediation, and other fixed-step systems here.
        // This event is raised from the main-thread MonoBehaviour that calls PumpClock.
        public static event Action<long> OnSimulationTick;

        #region Server Lifecycle
        public static async void Start(int port)
        {
            if (_isHosting)
            {
                Mod.LogError("[ServerHost] Server is already running.");
                return;
            }

            await PortForwarder.ForwardPort(port);

            try
            {
                _listener = new TcpListener(IPAddress.Any, port);
                _listener.Start();
                _isHosting = true;

                //More clock stuff
                _serverClock = new ServerClock(ServerTickRate);
                _serverClock.Start();
                Mod.Log($"[ServerHost] Server clock started at {ServerTickRate} ticks/second.");


                // 1. Set local Host ID = 0
                ClientConnection.SetLocalClientId(HostClientId);

                // 2. Register local Host craft into registry
                var localCraft = FlightSceneScript.Instance?.CraftNode as CraftNode;
                CraftRegistry.RegisterCraft(HostClientId, localCraft);

                // 3. Register local Host session
                lock (Sessions)
                {
                    Sessions.Clear();
                    Sessions.Add(new ClientSession
                    {
                        Id = HostClientId,
                        Client = null,
                        CraftXml = string.Empty
                    });
                }

                Mod.Log($"[ServerHost] Server listening on port {port}... Registered Host as ID {HostClientId}");

                while (_isHosting)
                {
                    TcpClient client = await _listener.AcceptTcpClientAsync();
                    _ = HandleClientAsync(client);
                }
            }
            catch (Exception ex)
            {
                if (_isHosting)
                    Mod.LogError($"[ServerHost] Error: {ex.Message}, server terminated.");
                else
                    Mod.Log($"[ServerHost] Server stopped.");
            }
        }

        public static void Stop()
        {
            if (!_isHosting)
            {
                Mod.Log("[ServerHost] Server is already stopped.");
                return;
            }

            Mod.Log("[ServerHost] Terminating server...");
            _isHosting = false;

            try
            {
                _listener?.Stop();
                _listener = null;
            }
            catch (Exception ex)
            {
                Mod.LogError($"[ServerHost] Error stopping listener: {ex.Message}");
            }

            lock (Sessions)
            {
                foreach (var session in Sessions)
                {
                    try
                    {
                        if (session.Client != null && session.Client.Connected)
                        {
                            session.Client.Close();
                            session.Client.Dispose();
                        }
                    }
                    catch (Exception ex)
                    {
                        Mod.LogError($"[ServerHost] Error closing client session {session.Id}: {ex.Message}");
                    }
                }
                Sessions.Clear();
            }

            CraftRegistry.ClearAll();
            _serverClock?.Stop();
            _serverClock = null;
            OnSimulationTick = null;
            Mod.Log("[ServerHost] Server stopped and cleaned up.");
        }
        #endregion


        #region Authoritative Simulation Clock
        // Runs due fixed simulation ticks. Call this once per rendered frame from a persistent Multiplayer MonoBehaviour's Update method, never from a TCP task.
        public static void PumpClock()
        {
            if (!_isHosting || _serverClock == null)
            {
                return;
            }

            int ticksToRun = _serverClock.GetDueTickCount(MaximumCatchUpTicksPerFrame);
            for (int i = 0; i < ticksToRun; i++)
            {
                if (!_serverClock.TryConsumeNextTick(out long tick))
                {
                    break;
                }

                // Systems that update Unity/Juno craft state should execute here on the game thread. Include this exact tick in telemetry and collision packets.
                OnSimulationTick?.Invoke(tick);
            }
        }

        // Returns the latest host-clock sample for connection setup, telemetry, and collision messages. It returns null until the host has started.
        public static ServerClockSnapshot? GetClockSnapshot()
        {
            return _serverClock == null ? (ServerClockSnapshot?)null : _serverClock.GetSnapshot();
        }
        #endregion


        #region Network Loop
        private static async Task HandleClientAsync(TcpClient client)
        {
            Mod.Log("[ServerHost] Remote client connected.");

            try
            {
                using (var receiver = new TcpNetworkReceiver())
                {
                    while (_isHosting && client.Connected)
                    {
                        var (data, metadata) = await receiver.ReceiveDataAsync(client);
                        if (data == null || metadata == null) break;

                        switch (metadata)
                        {
                            case "CONNECT":
                                await HandleConnect(client);
                                break;

                            case "CLIENT_CRAFT_DATA":
                                await HandleClientCraftData(client, data);
                                break;

                            default:
                                Mod.LogWarning($"[ServerHost] Unknown packet type received: {metadata}");
                                break;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                if (_isHosting) Mod.LogError($"[ServerHost] Client error: {ex.Message}");
            }
            finally
            {
                int clientId = GetClientId(client);
                if (clientId != -1)
                {
                    lock (Sessions)
                    {
                        Sessions.RemoveAll(s => s.Client == client);
                    }

                    // Despawn on Host and notify all remaining clients
                    CraftRegistry.DespawnCraft(clientId);
                    SendDataToClients(clientId.ToString(), "CLIENT_DISCONNECTED");
                }

                client.Close();
                Mod.Log($"[ServerHost] Client ID {clientId} disconnected.");
            }
        }

        private static async Task HandleConnect(TcpClient client)
        {
            try
            {
                _nextClientId++;

                lock (Sessions)
                {
                    Sessions.Add(new ClientSession
                    {
                        Id = _nextClientId,
                        Client = client,
                        CraftXml = string.Empty
                    });
                }

                byte[] responseBytes = NetworkSender.BuildPacket(_nextClientId.ToString(), "CONNECT_ACCEPTED");
                NetworkStream stream = client.GetStream();

                await stream.WriteAsync(responseBytes, 0, responseBytes.Length);
                await stream.FlushAsync();

                Mod.Log($"[ServerHost] Connection accepted for Client ID: {_nextClientId}");
            }
            catch (Exception ex)
            {
                Mod.LogError($"[ServerHost] Failed to send CONNECT_ACCEPTED: {ex.Message}");
            }
        }
        #endregion


        #region Receive Client Craft
        private static async Task HandleClientCraftData(TcpClient client, string data)
        {
            int clientId = GetClientId(client);
            if (clientId == -1) return;

            // 1. Save craft XML into session memory
            lock (Sessions)
            {
                var session = Sessions.Find(s => s.Id == clientId);
                if (session != null)
                {
                    session.CraftXml = data;
                }
            }

            // 2. Forward payload to ReceiveCraftData for decompression & spawning on Host
            await ReceiveCraftData.ProcessAndSpawnAsync(clientId, data);

            // 3. Ensure Host's own craft XML is cached in Session 0
            UpdateHostCraftXmlIfNeeded();

            // 4. Catch up new client with all existing crafts in session (Host + other players)
            lock (Sessions)
            {
                foreach (var session in Sessions)
                {
                    if (session.Id != clientId && !string.IsNullOrEmpty(session.CraftXml))
                    {
                        _ = SendCraftData.SendRawPayloadAsync(client, session.CraftXml, $"SPAWN_CRAFT:{session.Id}");
                    }
                }
            }

            // 5. Broadcast new client's craft XML to all other connected clients
            SendDataToClients(data, $"SPAWN_CRAFT:{clientId}", excludeClientId: clientId);

            //Start telemetry main handler
        }

        private static void UpdateHostCraftXmlIfNeeded()
        {
            lock (Sessions)
            {
                var hostSession = Sessions.Find(s => s.Id == HostClientId);
                if (hostSession != null && string.IsNullOrEmpty(hostSession.CraftXml))
                {
                    string hostXml = SendCraftData.GetLocalCraftXmlCompressed();
                    if (!string.IsNullOrEmpty(hostXml))
                    {
                        hostSession.CraftXml = hostXml;
                        Mod.Log("[ServerHost] Cached Host Craft XML to Session 0.");
                    }
                }
            }
        }
        #endregion


        #region Broadcast Helpers
        private static async Task SendPacketAsync(TcpClient client, string data, string metadata)
        {
            if (client == null || !client.Connected) return;

            try
            {
                byte[] packetBytes = NetworkSender.BuildPacket(data, metadata);
                NetworkStream stream = client.GetStream();
                await stream.WriteAsync(packetBytes, 0, packetBytes.Length);
                await stream.FlushAsync();
            }
            catch (Exception ex)
            {
                Mod.LogError($"[ServerHost] Send packet error ('{metadata}'): {ex.Message}");
            }
        }

        public static void SendDataToClients(string data, string metadata, int excludeClientId = -1)
        {
            lock (Sessions)
            {
                foreach (var session in Sessions)
                {
                    if (session.Id == excludeClientId) continue;
                    if (session.Client != null && session.Client.Connected)
                    {
                        _ = SendPacketAsync(session.Client, data, metadata);
                    }
                }
            }
        }

        private static int GetClientId(TcpClient client)
        {
            lock (Sessions)
            {
                var session = Sessions.Find(s => s.Client == client);
                return session != null ? session.Id : -1;
            }
        }
        #endregion

        private static void Update()
        {
            ServerConnection.PumpClock();
        }

    }
}