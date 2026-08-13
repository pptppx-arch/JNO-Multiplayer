namespace Assets.Scripts.Multiplayer
{
    using Assets.Scripts.Flight;
    using Assets.Scripts.Flight.Sim;
    using ModApi.Craft;
    using ModApi.State;
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.IO.Compression;
    using System.Net;
    using System.Net.Sockets;
    using System.Threading.Tasks;
    using System.Xml.Linq;
    using UnityEngine;

    public class ClientSession
    {
        public int Id { get; set; }
        public TcpClient Client { get; set; }
        public string CraftXml { get; set; }
    }

    public static class ServerHost
    {
        private static TcpListener _listener;
        private static bool _isHosting;

        public static List<ClientSession> Sessions { get; } = new List<ClientSession>();
        private static int _nextClientId = 0;

        // Starts the host TCP server and begins listening for incoming connections.
        public static async void Start(int port)
        {
            if (_isHosting)
            {
                Mod.LogError("[ServerHost] Server is already running.");
                return;
            }

            try
            {
                _listener = new TcpListener(IPAddress.Any, port);
                _listener.Start();
                _isHosting = true;

                Mod.Log($"[ServerHost] Server listening on port {port}...");

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


        #region Stops the host TCP server and cleans up resources.
        public static void Stop()
        {
            if (!_isHosting)
            {
                Mod.Log("[ServerHost] Server is already stopped.");
                return;
            }

            Mod.Log("[ServerHost] Terminating server...");
            _isHosting = false;

            // 1. Stop listening for new connections
            try
            {
                _listener?.Stop();
                _listener = null;
            }
            catch (Exception ex)
            {
                Mod.LogError($"[ServerHost] Error stopping listener: {ex.Message}");
            }

            // 2. Disconnect and cleanup all active client sessions
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

            Mod.Log("[ServerHost] Server stopped and cleaned up successfully.");
        }
        #endregion


        // Listens for incoming data from clients.
        private static async Task HandleClientAsync(TcpClient client)
        {
            Mod.Log("[ServerHost] Client connected.");

            try
            {
                using (var receiver = new NetworkReceiver())
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
                                await SpawnClientCraft(client, data);
                                break;

                            case "INPUTS":
                                HandleClientInputs(client, data);
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
                // Remove session tracking when client disconnects
                lock (Sessions)
                {
                    Sessions.RemoveAll(s => s.Client == client);
                }
                client.Close();
                Mod.Log("[ServerHost] Client disconnected.");
            }
        }


        #region Handles client data
        private static async Task HandleConnect(TcpClient client)
        {
            try
            {
                _nextClientId++;

                // Create a new session for the connected client
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

                Mod.Log($"Connection accepted and response sent to client with client ID: {_nextClientId}");
            }
            catch (Exception ex)
            {
                Mod.LogError($"[ServerHost] Failed to send CONNECT_ACCEPTED: {ex.Message}");
            }
        }
        private static async Task SpawnClientCraft(TcpClient client, string data)
        {
            try
            {
                // 1. Find active session for this client
                ClientSession session;
                lock (Sessions)
                {
                    session = Sessions.Find(s => s.Client == client);
                }

                if (session == null)
                {
                    Mod.LogError("[ServerHost] Received craft data from an untracked client.");
                    return;
                }

                // 2. Decompress Base64 / GZip string if compressed, or use directly if raw XML
                string xmlString = data;
                if (!xmlString.TrimStart().StartsWith("<"))
                {
                    try
                    {
                        byte[] compressedBytes = Convert.FromBase64String(data);
                        using (MemoryStream ms = new MemoryStream(compressedBytes))
                        using (GZipStream gzip = new GZipStream(ms, CompressionMode.Decompress))
                        using (StreamReader reader = new StreamReader(gzip, System.Text.Encoding.UTF8))
                        {
                            xmlString = reader.ReadToEnd();
                        }
                    }
                    catch (Exception ex)
                    {
                        Mod.LogError($"[ServerHost] Failed to decompress craft XML for Client {session.Id}: {ex.Message}");
                        return;
                    }
                }

                // 3. Store craft XML in session
                session.CraftXml = xmlString;
                Mod.Log($"[ServerHost] Received valid craft XML for Client {session.Id}. Spawning craft...");

                // 4. Ensure FlightScene is loaded before attempting to spawn
                var flightScene = FlightSceneScript.Instance.CraftNode;
                if (flightScene == null || flightScene == null)
                {
                    Mod.LogWarning("[ServerHost] Flight scene or host craft not ready. Skipping craft spawn.");
                    return;
                }

                // 5. Parse XML & Load CraftData via Juno API
                XElement xml = XElement.Parse(xmlString);
                CraftData craftData = Game.Instance.CraftLoader.LoadCraftImmediate(xml);

                // 6. Create a dynamic LaunchLocation offset slightly from host to prevent collision
                Vector3d spawnPosition = flightScene.Position + new Vector3d(50, 0, 0);

                LaunchLocation launchLocation = LaunchLocation.CreateLaunchLocation(
                    $"Client_{session.Id}_Craft",
                    flightScene.Parent,
                    spawnPosition,
                    flightScene.Velocity,
                    flightScene.Heading,
                    flightScene.ReferenceFrame,
                    LaunchLocationType.SurfaceLockedGround
                );

                // 7. Spawn craft into the game world
                CraftNode clientCraftNode = FlightSceneScript.Instance.SpawnCraft(session.Client.ToString(),craftData, launchLocation);
                Mod.Log($"[ServerHost] Successfully spawned craft for Client {session.Client}.");
            }
            catch (Exception ex)
            {
                Mod.LogError($"[ServerHost] Error spawning client craft: {ex.Message}");
            }

            await Task.CompletedTask;
        }
        private static void HandleClientInputs(TcpClient client, string data)
        {

        }
        #endregion
    }
}