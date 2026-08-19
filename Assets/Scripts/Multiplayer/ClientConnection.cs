namespace Assets.Scripts.Multiplayer
{
    using Assets.Scripts.Multiplayer.CraftData;
    using Assets.Scripts.Multiplayer.Telemetry;
    using Assets.Scripts.Threading;
    using System;
    using System.Net;
    using System.Net.Sockets;
    using System.Threading.Tasks;

    public static class ClientConnection
    {
        public static TcpClient ActiveClient { get; private set; }
        public static int LocalClientId { get; private set; } = -1;
        public static bool IsConnected => ActiveClient != null && ActiveClient.Connected;
        public static string hostIp;
        public static int TcpPort { get; private set; }

        private static ClientTelemetryUpdater _telemetryUpdater;
        private static bool _initialCraftUploadPending;
        private static SerializedTcpWriter _outboundWriter;

        public static void SetLocalClientId(int id)
        {
            LocalClientId = id;
        }

        #region Connection lifecycle
        public static async void Connect(string host, int port)
        {
            hostIp = host;
            TcpPort = port;

            if (IsConnected)
            {
                Mod.LogWarning("[ClientConnection] Already connected to a server.");
                return;
            }

            var networkSender = new NetworkSender();
            var (sentSuccess, client) = await networkSender.ConnectAndSendDataAsync(
                host,
                string.Empty,
                "CONNECT",
                port);

            if (sentSuccess && client != null)
            {
                ActiveClient = client;
                _outboundWriter = new SerializedTcpWriter(client, "Host server");
                _outboundWriter.Start();

                Mod.Log("[ClientConnection] Connected; waiting for host to accept conection.");
                _ = StartListeningAsync(client);
            }
            else
            {
                Mod.LogError("[ClientConnection] Failed to connect to server.");
            }
        }

        public static void Disconnect()
        {
            int localClientId = LocalClientId;
            _initialCraftUploadPending = false;

            _telemetryUpdater?.Dispose();
            _telemetryUpdater = null;

            if (ActiveClient != null)
            {
                try
                {
                    _outboundWriter?.Dispose();
                    _outboundWriter = null;
                    ActiveClient = null;
                    ActiveClient?.Dispose();
                }
                catch (Exception ex)
                {
                    Mod.LogError($"[ClientConnection] TCP disconnect error: {ex.Message}");
                }
                finally
                {
                    ActiveClient = null;
                }
            }

            LocalClientId = -1;

            // This destroys remote proxies on the next game-thread update while preserving
            // the local Juno-owned craft identified before LocalClientId was reset.
            MultiplayerThread.Post(() => CraftRegistry.ClearAllExcept(localClientId));
            Mod.Log("[ClientConnection] Disconnected; client telemetry stopped.");
        }

        /// <summary>
        /// Called every frame by MultiplayerTelemetryRuntime.Update() on the game thread.
        /// </summary>
        public static void PumpTelemetry(float deltaTime)
        {
            _telemetryUpdater?.Pump(deltaTime);
        }
        #endregion

        #region TCP receive loop
        private static async Task StartListeningAsync(TcpClient client)
        {
            try
            {
                using (var receiver = new TcpNetworkReceiver())
                {
                    while (client.Connected)
                    {
                        var (data, metadata) = await receiver.ReceiveDataAsync(client);
                        if (data == null || metadata == null) break;
                        await ProcessIncomingPacket(data, metadata);
                    }
                }
            }
            catch (Exception ex)
            {
                Mod.LogError($"[ClientConnection] TCP listening error: {ex.Message}");
            }
            finally
            {
                Disconnect();
            }
        }

        private static async Task ProcessIncomingPacket(string data, string metadata)
        {
            try
            {
                if (metadata.StartsWith("SPAWN_CRAFT:"))
                {
                    if (int.TryParse(
                            metadata.Substring("SPAWN_CRAFT:".Length),
                            out int remoteClientId)
                        && remoteClientId >= 0
                        && remoteClientId != LocalClientId)
                    {
                        // XML parsing/decompression stays in this path. The actual Juno
                        // spawn is queued and completed by MultiplayerThread.Pump().
                        await ReceiveCraftData.ProcessAndSpawnAsync(remoteClientId, data);
                    }

                    return;
                }

                switch (metadata)
                {
                    case "CONNECT_ACCEPTED":
                        if (!int.TryParse(data, out int assignedId) || assignedId < 0)
                        {
                            Mod.LogError("[ClientConnection] Invalid assigned Client ID.");
                            Disconnect();
                            return;
                        }

                        LocalClientId = assignedId;
                        _initialCraftUploadPending = true;
                        StartTelemetryAfterHandshake();
                        break;

                    case "CLIENT_DISCONNECTED":
                        if (int.TryParse(data, out int departedClientId) && departedClientId >= 0)
                        {
                            MultiplayerThread.Post(() => CraftRegistry.DespawnCraft(departedClientId));
                        }
                        break;

                    default:
                        Mod.LogWarning($"[ClientConnection] Unknown TCP metadata: {metadata}");
                        break;
                }
            }
            catch (Exception ex)
            {
                Mod.LogError($"[ClientConnection] TCP packet processing error ({metadata}): {ex.Message}");
            }
        }
        #endregion

        #region Game-thread work
        /// <summary>
        /// Called once per frame by MultiplayerTelemetryRuntime.Update(). Captures local
        /// craft XML only after that runtime has registered a flight-ready local craft.
        /// </summary>
        public static void PumpGameThread()
        {
            if (!_initialCraftUploadPending || LocalClientId < 0) return;

            TcpClient client = ActiveClient;
            if (client == null || !client.Connected)
            {
                _initialCraftUploadPending = false;
                return;
            }

            if (CraftRegistry.GetCraft(LocalClientId) == null)
            {
                return;
            }

            string compressedXml = SendCraftData.GetLocalCraftXmlCompressedOnGameThread();
            if (string.IsNullOrEmpty(compressedXml))
            {
                // Flight may still be finishing setup. Retry next game frame.
                return;
            }

            _initialCraftUploadPending = false;
            _ = SendInitialCraftXmlAsync(client, compressedXml);
        }

        private static Task SendInitialCraftXmlAsync(TcpClient client, string compressedXml)
        {
            if (client == null
                || client != ActiveClient
                || string.IsNullOrEmpty(compressedXml))
            {
                return Task.CompletedTask;
            }

            if (_outboundWriter == null
                || !_outboundWriter.Enqueue(compressedXml, "CLIENT_CRAFT_DATA"))
            {
                Mod.LogWarning("[ClientConnection] Could not queue initial craft XML.");
            }

            return Task.CompletedTask;
        }
        #endregion

        private static void StartTelemetryAfterHandshake()
        {
            if (_telemetryUpdater != null || LocalClientId < 0) return;

            try
            {
                IPAddress hostAddress = ResolveHostAddress(hostIp);
                _telemetryUpdater = new ClientTelemetryUpdater(
                    new IPEndPoint(hostAddress, TcpPort),
                    LocalClientId);
                _telemetryUpdater.Start();
                Mod.Log($"[ClientConnection] Client telemetry started for Client ID {LocalClientId}.");
            }
            catch (Exception ex)
            {
                Mod.LogError($"[ClientConnection] Failed to start UDP telemetry: {ex.Message}");
                _telemetryUpdater?.Dispose();
                _telemetryUpdater = null;
            }
        }

        private static IPAddress ResolveHostAddress(string host)
        {
            if (IPAddress.TryParse(host, out IPAddress address)) return address;

            IPAddress[] addresses = Dns.GetHostAddresses(host);
            if (addresses == null || addresses.Length == 0)
            {
                throw new InvalidOperationException("Host name resolved to no IP address.");
            }

            foreach (IPAddress candidate in addresses)
            {
                if (candidate.AddressFamily == AddressFamily.InterNetwork) return candidate;
            }

            return addresses[0];
        }
    }
}
