namespace Assets.Scripts.Multiplayer
{
    using Assets.Scripts.Clock;
    using Assets.Scripts.Flight.Sim;
    using Assets.Scripts.Multiplayer.CraftData;
    using Assets.Scripts.Multiplayer.Telemetry;
    using Assets.Scripts.Threading;
    using System;
    using System.Collections.Generic;
    using System.Net;
    using System.Net.Sockets;
    using System.Threading.Tasks;
    using System.Security.Cryptography;
    //RAH RAH RAH

    public sealed class ClientSession : IDisposable
    {
        public int Id { get; set; }
        public TcpClient Client { get; set; }
        public string CraftXml { get; set; }
        public bool IsHost => Client == null;
        public string UdpSessionToken { get; set; }

        private SerializedTcpWriter _writer;

        public void StartWriter()
        {
            if (IsHost || _writer != null) return;

            _writer = new SerializedTcpWriter(Client, $"Client ID {Id}");
            _writer.Start();
        }

        public bool EnqueuePacket(string data, string metadata)
        {
            return _writer != null && _writer.Enqueue(data, metadata);
        }

        public void Dispose()
        {
            _writer?.Dispose();
            _writer = null;
        }
    }

    public static class ServerConnection
    {
        public const int HostClientId = 0;
        private const int ServerTickRate = 60;
        private const int MaximumCatchUpTicksPerFrame = 4;

        private static TcpListener _listener;
        private static bool _isHosting;
        private static int _telemetryPort;
        private static ServerClock _serverClock;
        private static HostTelemetryUpdater _telemetryUpdater;
        private static TaskCompletionSource<string> _hostCraftXmlReady;

        public static bool IsHosting => _isHosting;
        public static List<ClientSession> Sessions { get; } = new List<ClientSession>();

        private static int _nextClientId = HostClientId;

        // Raised only by PumpClock(), which is invoked from the persistent game runtime.
        public static event Action<long> OnSimulationTick;


        #region Server lifecycle
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
                _telemetryPort = port;
                _serverClock = new ServerClock(ServerTickRate);
                _serverClock.Start();

                lock (Sessions)
                {
                    Sessions.Clear();
                    _nextClientId = HostClientId;
                    Sessions.Add(new ClientSession
                    {
                        Id = HostClientId,
                        Client = null,
                        CraftXml = string.Empty
                    });
                }

                // Completed by PumpGameThread after the persistent runtime has registered
                // an actual host CraftNode and Juno can provide XML.
                _hostCraftXmlReady = new TaskCompletionSource<string>();

                Mod.Log($"[ServerHost] TCP listener started on {port}. Host is Client ID {HostClientId}.");
                while (_isHosting)
                {
                    TcpClient client = await _listener.AcceptTcpClientAsync();
                    _ = HandleClientAsync(client);
                }
            }
            catch (Exception ex)
            {
                if (_isHosting)
                {
                    Mod.LogError($"[ServerHost] Error: {ex.Message}");
                }
                else
                {
                    Mod.Log("[ServerHost] Server stopped.");
                }
            }
        }

        public static void Stop()
        {
            if (!_isHosting)
            {
                Mod.Log("[ServerHost] Server is already stopped.");
                return;
            }

            _isHosting = false;
            _hostCraftXmlReady?.TrySetResult(null);
            _hostCraftXmlReady = null;

            try
            {
                _listener?.Stop();
                _listener = null;
            }
            catch (Exception ex)
            {
                Mod.LogError($"[ServerHost] Error stopping TCP listener: {ex.Message}");
            }

            if (_telemetryUpdater != null)
            {
                OnSimulationTick -= _telemetryUpdater.Pump;
                _telemetryUpdater.Dispose();
                _telemetryUpdater = null;
            }

            lock (Sessions)
            {
                foreach (ClientSession session in Sessions)
                {
                    try
                    {
                        session.Client?.Dispose();
                    }
                    catch (Exception ex)
                    {
                        Mod.LogError($"[ServerHost] Error closing Client ID {session.Id}: {ex.Message}");
                    }
                }

                Sessions.Clear();
            }

            // The host state is already false when the queued action runs, so preserve
            // Client ID 0 explicitly rather than asking the registry to infer the role.
            MultiplayerThread.Post(() => CraftRegistry.ClearAllExcept(HostClientId));

            _serverClock?.Stop();
            _serverClock = null;
            OnSimulationTick = null;
            Mod.Log("[ServerHost] Server and telemetry relay stopped.");
        }
        #endregion

        #region Authoritative clock
        public static void PumpClock()
        {
            if (!_isHosting || _serverClock == null) return;

            int ticksToRun = _serverClock.GetDueTickCount(MaximumCatchUpTicksPerFrame);
            for (int i = 0; i < ticksToRun; i++)
            {
                if (!_serverClock.TryConsumeNextTick(out long tick)) break;
                OnSimulationTick?.Invoke(tick);
            }
        }

        public static ServerClockSnapshot? GetClockSnapshot()
        {
            return _serverClock == null
                ? (ServerClockSnapshot?)null
                : _serverClock.GetSnapshot();
        }
        #endregion

        #region Session helpers
        public static bool IsRemoteSessionActive(int clientId)
        {
            lock (Sessions)
            {
                ClientSession session = Sessions.Find(s => s.Id == clientId);
                return session != null
                    && !session.IsHost
                    && session.Client != null
                    && session.Client.Connected;
            }
        }

        public static bool IsExpectedClientAddress(int clientId, IPAddress remoteAddress)
        {
            if (remoteAddress == null) return false;

            lock (Sessions)
            {
                ClientSession session = Sessions.Find(s => s.Id == clientId);
                IPEndPoint tcpEndPoint = session == null || session.Client == null
                    ? null
                    : session.Client.Client.RemoteEndPoint as IPEndPoint;
                return tcpEndPoint != null && tcpEndPoint.Address.Equals(remoteAddress);
            }
        }
        public static bool IsExpectedUdpToken(int clientId, string suppliedToken)
        {
            lock (Sessions)
            {
                ClientSession session = Sessions.Find(s => s.Id == clientId);
                return session != null
                    && !session.IsHost
                    && TelemetryPacket.TokensEqual(session.UdpSessionToken, suppliedToken);
            }
        }

        private static string CreateUdpSessionToken()
        {
            byte[] randomBytes = new byte[32];
            using (RandomNumberGenerator random = RandomNumberGenerator.Create())
            {
                random.GetBytes(randomBytes);
            }

            // 32 random bytes encoded as URL-safe Base64, without '=' padding: 43 characters.
            return Convert.ToBase64String(randomBytes)
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');
        }
        #endregion

        #region TCP receive loop
        private static async Task HandleClientAsync(TcpClient client)
        {
            try
            {
                using (var receiver = new TcpNetworkReceiver())
                {
                    while (_isHosting && client.Connected)
                    {
                        var (data, metadata) = await receiver.ReceiveDataAsync(client);
                        if (data == null || metadata == null || metadata.Length > 256 || data.Length > 2 * 1024 * 1024) break;

                        switch (metadata)
                        {
                            case "CONNECT":
                                await HandleConnectAsync(client);
                                break;

                            case "CLIENT_CRAFT_DATA":
                                await HandleClientCraftDataAsync(client, data);
                                break;

                            default:
                                Mod.LogWarning($"[ServerHost] Unknown TCP metadata: {metadata}");
                                break;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                if (_isHosting)
                {
                    Mod.LogError($"[ServerHost] Client TCP error: {ex.Message}");
                }
            }
            finally
            {
                ClientSession departedSession = FindSession(client);
                int clientId = departedSession == null ? -1 : departedSession.Id;

                if (clientId != -1)
                {
                    lock (Sessions)
                    {
                        Sessions.Remove(departedSession);
                    }

                    departedSession.Dispose();
                    _telemetryUpdater?.RemoveClient(clientId);
                    MultiplayerThread.Post(() => CraftRegistry.DespawnCraft(clientId));
                    SendDataToClients(clientId.ToString(), "CLIENT_DISCONNECTED");
                }
            }
        }

        private static Task HandleConnectAsync(TcpClient client)
        {
            ClientSession session;

            lock (Sessions)
            {
                int assignedId = ++_nextClientId;

                session = new ClientSession
                {
                    Id = assignedId,
                    Client = client,
                    CraftXml = string.Empty,
                    UdpSessionToken = CreateUdpSessionToken()
                };

                Sessions.Add(session);
            }

            // Start the one and only code path allowed to write to this client's TCP stream.
            session.StartWriter();

            EnsureHostTelemetryStarted();

            string connectAcceptedPayload = session.Id + "|" + session.UdpSessionToken;
            if (!session.EnqueuePacket(session.Id.ToString(), "CONNECT_ACCEPTED"))
            {
                Mod.LogWarning($"[ServerHost] Could not queue CONNECT_ACCEPTED for Client ID {session.Id}.");
            }
            else
            {
                Mod.Log($"[ServerHost] Assigned Client ID {session.Id}; CONNECT_ACCEPTED queued.");
            }

            return Task.CompletedTask;
        }


        private static void EnsureHostTelemetryStarted()
        {
            if (_telemetryUpdater != null) return;

            try
            {
                _telemetryUpdater = new HostTelemetryUpdater(
                    _telemetryPort,
                    HostClientId,
                    ServerTickRate);
                _telemetryUpdater.Start();
                OnSimulationTick += _telemetryUpdater.Pump;
            }
            catch (Exception ex)
            {
                Mod.LogError($"[ServerHost] Failed to start UDP telemetry relay: {ex.Message}");
                _telemetryUpdater = null;
            }
        }
        #endregion

        #region Craft XML transfer
        private static async Task HandleClientCraftDataAsync(TcpClient client, string data)
        {
            int clientId = GetClientId(client);
            if (clientId == -1) return;

            lock (Sessions)
            {
                ClientSession session = Sessions.Find(s => s.Id == clientId);
                if (session != null)
                {
                    session.CraftXml = data;
                }
            }

            // The actual Juno spawn runs when MultiplayerThread.Pump executes on Update().
            CraftNode spawnedProxy = await ReceiveCraftData.ProcessAndSpawnAsync(clientId, data);
            if (spawnedProxy == null || !_isHosting || !client.Connected)
            {
                Mod.LogWarning($"[ServerHost] Did not complete proxy spawn for Client ID {clientId}.");
                return;
            }

            string hostXml = await WaitForHostCraftXmlAsync();
            if (!_isHosting || !client.Connected || string.IsNullOrEmpty(hostXml))
            {
                return;
            }

            var existingCrafts = new List<KeyValuePair<int, string>>();
            lock (Sessions)
            {
                foreach (ClientSession session in Sessions)
                {
                    if (session.Id != clientId && !string.IsNullOrEmpty(session.CraftXml))
                    {
                        existingCrafts.Add(new KeyValuePair<int, string>(session.Id, session.CraftXml));
                    }
                }
            }

            foreach (KeyValuePair<int, string> existingCraft in existingCrafts)
            {
                var _client = FindSession(client);
                _client.EnqueuePacket(existingCraft.Value, $"SPAWN_CRAFT:{existingCraft.Key}");
            }

            SendDataToClients(data, $"SPAWN_CRAFT:{clientId}", excludeClientId: clientId);
        }
        #endregion

        #region TCP send helpers

        public static void SendDataToClients(string data, string metadata, int excludeClientId = -1)
        {
            var destinations = new List<ClientSession>();
            lock (Sessions) 
            {
                foreach (ClientSession session in Sessions)
                {
                    if (session.Id == excludeClientId || session.IsHost) continue;
                    if (session.Client != null && session.Client.Connected) destinations.Add(session);
                }
            }

            foreach (ClientSession session in destinations)
            {
                if (!session.EnqueuePacket(data, metadata)) Mod.LogWarning($"[ServerHost] Could not queue '{metadata}' for Client ID {session.Id}.");
            }
        }

        private static ClientSession FindSession(TcpClient client)
        {
            lock (Sessions)
            {
                return Sessions.Find(s => s.Client == client);
            }
        }
        private static int GetClientId(TcpClient client)
        {
            ClientSession session = FindSession(client);
            return session == null ? -1 : session.Id;
        }
        #endregion

        #region Game-thread work
        /// <summary>
        /// Called once per rendered frame by MultiplayerTelemetryRuntime.Update().
        /// Captures host XML only when the runtime has registered an actual local host craft.
        /// </summary>
        public static void PumpGameThread()
        {
            if (!_isHosting) return;

            TaskCompletionSource<string> completion = _hostCraftXmlReady;
            if (completion == null || completion.Task.IsCompleted) return;

            if (CraftRegistry.GetCraft(HostClientId) == null)
            {
                return;
            }

            string hostXml = SendCraftData.GetLocalCraftXmlCompressedOnGameThread();
            if (string.IsNullOrEmpty(hostXml))
            {
                return;
            }

            lock (Sessions)
            {
                ClientSession hostSession = Sessions.Find(s => s.Id == HostClientId);
                if (hostSession != null)
                {
                    hostSession.CraftXml = hostXml;
                }
            }

            completion.TrySetResult(hostXml);
            Mod.Log("[ServerHost] Host craft XML captured on the game thread.");
        }

        private static Task<string> WaitForHostCraftXmlAsync()
        {
            lock (Sessions)
            {
                ClientSession hostSession = Sessions.Find(s => s.Id == HostClientId);
                if (hostSession != null && !string.IsNullOrEmpty(hostSession.CraftXml))
                {
                    return Task.FromResult(hostSession.CraftXml);
                }
            }

            TaskCompletionSource<string> completion = _hostCraftXmlReady;
            return completion == null
                ? Task.FromResult<string>(null)
                : completion.Task;
        }
        #endregion
    }
}