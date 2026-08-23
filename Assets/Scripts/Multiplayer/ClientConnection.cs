namespace Assets.Scripts.Multiplayer
{
    using Assets.Scripts.Multiplayer.CraftData;
    using Assets.Scripts.Multiplayer.Telemetry;
    using Assets.Scripts.Clock;
    using Assets.Scripts.Threading;
    using System;
    using System.Net;
    using System.Net.Sockets;
    using System.Threading.Tasks;

    public static class ClientConnection
    {
        private enum ClientConnectionState
        {
            Disconnected,
            AwaitingAccepted,
            Accepted,
            CraftQueued,
            Closing
        }

        public static TcpClient ActiveClient { get; private set; }
        public static int LocalClientId { get; private set; } = -1;
        public static bool IsConnected => ActiveClient != null && ActiveClient.Connected;
        public static string hostIp;
        public static int TcpPort { get; private set; }

        private static ClientTelemetryUpdater _telemetryUpdater;
        private static bool _initialCraftUploadPending;
        private static SerializedTcpWriter _outboundWriter;
        private static string _udpSessionToken;
        private static ClientClockSynchronizer _clockSynchronizer;
        private static ClientConnectionState _state = ClientConnectionState.Disconnected;

        public static void SetLocalClientId(int id)
        {
            LocalClientId = id;
        }

        #region Connection lifecycle
        public static async void Connect(string host, int port)
        {
            hostIp = host;
            TcpPort = port;

            if (IsConnected || _state != ClientConnectionState.Disconnected)
            {
                Mod.LogWarning("[ClientConnection] Already connecting or connected to a server.");
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
                _state = ClientConnectionState.AwaitingAccepted;

                Mod.Log("[ClientConnection] Connected; waiting for host to accept connection.");
                _ = StartListeningAsync(client);
            }
            else
            {
                Mod.LogError("[ClientConnection] Failed to connect to server.");
            }
        }

        public static void Disconnect()
        {
            bool hadClientState = ActiveClient != null
                || _outboundWriter != null
                || _telemetryUpdater != null
                || LocalClientId >= 0;
            if (!hadClientState) return;

            int localClientId = LocalClientId;
            _state = ClientConnectionState.Closing;
            _initialCraftUploadPending = false;

            _telemetryUpdater?.Dispose();
            _telemetryUpdater = null;

            try
            {
                _outboundWriter?.Dispose();
                _outboundWriter = null;

                TcpClient client = ActiveClient;
                ActiveClient = null;
                client?.Dispose();
            }
            catch (Exception ex)
            {
                Mod.LogError($"[ClientConnection] TCP disconnect error: {ex.Message}");
            }
            finally
            {
                ActiveClient = null;
            }

            _clockSynchronizer?.Stop();
            _clockSynchronizer = null;
            LocalClientId = -1;
            _udpSessionToken = null;
            _state = ClientConnectionState.Disconnected;

            // The runtime shutdown path clears proxies synchronously, while this queued cleanup
            // preserves the existing standalone-disconnect behavior.
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
                if (TryParseRemoteCraftMessage(metadata, out int remoteClientId))
                {
                    if (_state != ClientConnectionState.Accepted
                        && _state != ClientConnectionState.CraftQueued)
                    {
                        Mod.LogWarning("[ClientConnection] Rejected remote craft XML before CONNECT_ACCEPTED.");
                        Disconnect();
                        return;
                    }

                    if (remoteClientId >= 0 && remoteClientId != LocalClientId)
                    {
                        // XML parsing/decompression stays in this path. The actual Juno
                        // spawn or replacement is queued for MultiplayerThread.Pump().
                        await ReceiveCraftData.ProcessAndSpawnAsync(remoteClientId, data);
                    }

                    return;
                }

                switch (metadata)
                {
                    case "CONNECT_ACCEPTED":
                        if (_state != ClientConnectionState.AwaitingAccepted
                            || !TryParseConnectAccepted(data, out int assignedId, out string udpSessionToken))
                        {
                            Mod.LogError("[ClientConnection] Invalid CONNECT_ACCEPTED client ID or UDP session token.");
                            Disconnect();
                            return;
                        }

                        LocalClientId = assignedId;
                        _udpSessionToken = udpSessionToken;
                        _state = ClientConnectionState.Accepted;
                        _clockSynchronizer = new ClientClockSynchronizer();
                        _clockSynchronizer.Start();
                        _initialCraftUploadPending = true;
                        StartTelemetryAfterHandshake();
                        RequestClockSync();
                        break;

                    case "CLIENT_DISCONNECTED":
                        if ((_state == ClientConnectionState.Accepted
                                || _state == ClientConnectionState.CraftQueued)
                            && int.TryParse(data, out int departedClientId)
                            && departedClientId >= 0)
                        {
                            MultiplayerThread.Post(() => CraftRegistry.DespawnCraft(departedClientId));
                        }
                        break;

                    case "CLOCK_SYNC_RESPONSE":
                        if (!TryConsumeClockSyncResponse(data))
                        {
                            Mod.LogWarning("[ClientConnection] Rejected invalid CLOCK_SYNC_RESPONSE.");
                            Disconnect();
                        }
                        break;

                    default:
                        Mod.LogWarning($"[ClientConnection] Rejected unknown TCP metadata: {metadata}");
                        Disconnect();
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
            _state = ClientConnectionState.CraftQueued;
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
        /// <summary>
        /// Queues a replacement XML payload for this client's one locally launched craft.
        /// The serialized TCP writer preserves ordering after the initial CLIENT_CRAFT_DATA
        /// frame, so an early refresh cannot overtake the handshake craft payload.
        /// </summary>
        public static bool QueueLocalCraftXmlUpdateOnGameThread(string compressedXml)
        {
            if (string.IsNullOrEmpty(compressedXml)
                || LocalClientId < 0
                || _state != ClientConnectionState.CraftQueued
                || ActiveClient == null
                || !ActiveClient.Connected
                || _outboundWriter == null)
            {
                return false;
            }

            if (!_outboundWriter.Enqueue(compressedXml, "CLIENT_CRAFT_UPDATE"))
            {
                Mod.LogWarning("[ClientConnection] Could not queue refreshed craft XML.");
                return false;
            }

            Mod.Log("[ClientConnection] Queued refreshed local craft XML.");
            return true;
        }
        #endregion

        #region Helper methods
        private static void StartTelemetryAfterHandshake()
        {
            if (_telemetryUpdater != null || LocalClientId < 0) return;

            try
            {
                IPAddress hostAddress = ResolveHostAddress(hostIp);
                _telemetryUpdater = new ClientTelemetryUpdater(
                    new IPEndPoint(hostAddress, TcpPort),
                    LocalClientId,
                    _udpSessionToken,
                    _clockSynchronizer);
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

        private static void RequestClockSync()
        {
            if ((_state != ClientConnectionState.Accepted
                    && _state != ClientConnectionState.CraftQueued)
                || _outboundWriter == null)
            {
                return;
            }

            long requestTimestamp = ClientClockSynchronizer.CreateRequestTimestamp();
            if (!_outboundWriter.Enqueue(
                    requestTimestamp.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    "CLOCK_SYNC_REQUEST"))
            {
                Mod.LogWarning("[ClientConnection] Could not queue CLOCK_SYNC_REQUEST.");
            }
        }

        private static bool TryConsumeClockSyncResponse(string data)
        {
            if ((_state != ClientConnectionState.Accepted
                    && _state != ClientConnectionState.CraftQueued)
                || _clockSynchronizer == null
                || string.IsNullOrEmpty(data))
            {
                return false;
            }

            string[] fields = data.Split('|');
            if (fields.Length != 3
                || !long.TryParse(
                    fields[0],
                    System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out long requestTimestamp)
                || !long.TryParse(
                    fields[1],
                    System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out long serverTick)
                || !int.TryParse(
                    fields[2],
                    System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out int serverTickRate)
                || requestTimestamp <= 0
                || serverTick < 0
                || serverTickRate <= 0)
            {
                return false;
            }

            _clockSynchronizer.ObserveRoundTrip(requestTimestamp, serverTick, serverTickRate);
            return true;
        }

        private static bool TryParseRemoteCraftMessage(string metadata, out int remoteClientId)
        {
            remoteClientId = -1;
            if (string.IsNullOrEmpty(metadata)) return false;

            const string spawnPrefix = "SPAWN_CRAFT:";
            const string updatePrefix = "UPDATE_CRAFT:";
            string idText;
            if (metadata.StartsWith(spawnPrefix, StringComparison.Ordinal))
            {
                idText = metadata.Substring(spawnPrefix.Length);
            }
            else if (metadata.StartsWith(updatePrefix, StringComparison.Ordinal))
            {
                idText = metadata.Substring(updatePrefix.Length);
            }
            else
            {
                return false;
            }

            return int.TryParse(idText, out remoteClientId);
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
        private static bool TryParseConnectAccepted(string data, out int assignedId, out string udpSessionToken)
        {
            assignedId = -1;
            udpSessionToken = null;
            if (string.IsNullOrEmpty(data)) return false;

            string[] fields = data.Split('|');
            if (fields.Length != 2
                || !int.TryParse(fields[0], out assignedId)
                || assignedId < 0
                || !TelemetryPacket.IsValidSessionToken(fields[1]))
            {
                assignedId = -1;
                return false;
            }

            udpSessionToken = fields[1];
            return true;
        }
        #endregion
    }
}
