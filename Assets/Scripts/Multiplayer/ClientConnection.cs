namespace Assets.Scripts.Multiplayer
{
    using Assets.Scripts.Flight;
    using Assets.Scripts.Flight.Sim;
    using Assets.Scripts.Multiplayer.CraftData;
    using Assets.Scripts.Multiplayer.Telemetry;
    using System;
    using System.Net.Sockets;
    using System.Threading.Tasks;

    public static class ClientConnection
    {
        public static TcpClient ActiveClient { get; private set; }
        public static int LocalClientId { get; private set; } = -1;
        public static bool IsConnected => ActiveClient != null && ActiveClient.Connected;
        public static string hostIp;
        public static int TcpPort { get; private set; }

        public static void SetLocalClientId(int id)
        {
            LocalClientId = id;
        }

        #region Connection Lifecycle
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
            var (sentSuccess, client) = await networkSender.ConnectAndSendDataAsync(host, string.Empty, "CONNECT", port);

            if (sentSuccess && client != null)
            {
                ActiveClient = client;
                Mod.Log("[ClientConnection] Connected! Starting listening loop...");
                _ = StartListeningAsync(ActiveClient);
            }
            else
            {
                Mod.LogError("[ClientConnection] Failed to connect to server.");
            }
        }

        public static void Disconnect()
        {
            if (ActiveClient != null)
            {
                try
                {
                    ActiveClient.Close();
                    ActiveClient.Dispose();
                }
                catch (Exception ex)
                {
                    Mod.LogError($"[ClientConnection] Error during disconnect: {ex.Message}");
                }
                finally
                {
                    ActiveClient = null;
                    LocalClientId = -1;
                    CraftRegistry.ClearAll();
                    TelemetryClient.StopTelemetry();
                    Mod.Log("[ClientConnection] Disconnected from server.");
                }
            }
        }
        #endregion


        #region Network Loop
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
                Mod.LogError($"[ClientConnection] Listening loop error: {ex.Message}");
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
                // Relay spawn packet: "SPAWN_CRAFT:<clientId>"
                if (metadata.StartsWith("SPAWN_CRAFT:"))
                {
                    if (int.TryParse(metadata.Substring("SPAWN_CRAFT:".Length), out int remoteClientId))
                    {
                        Mod.Log($"[ClientConnection] Received craft XML relay for Client ID {remoteClientId}.");
                        await ReceiveCraftData.ProcessAndSpawnAsync(remoteClientId, data);
                    }
                    return;
                }

                switch (metadata)
                {
                    case "CONNECT_ACCEPTED":
                        if (int.TryParse(data, out int assignedId))
                        {
                            LocalClientId = assignedId;
                            Mod.Log($"[ClientConnection] Connection accepted! Assigned Client ID: {LocalClientId}");

                            // Register local craft in registry
                            var localCraft = FlightSceneScript.Instance?.CraftNode as CraftNode;
                            CraftRegistry.RegisterCraft(LocalClientId, localCraft);

                            await CraftData.SendCraftData.SendLocalCraftAsync(ActiveClient, "CLIENT_CRAFT_DATA");
                        }
                        else
                        {
                            Mod.LogError("[ClientConnection] Failed to parse assigned Client ID. Disconnecting...");
                            Disconnect();
                        }
                        break;

                    case "CLIENT_DISCONNECTED":
                        if (int.TryParse(data, out int disconnectedId))
                        {
                            CraftRegistry.DespawnCraft(disconnectedId);
                        }
                        break;

                    case "TELEMETRY_START":
                        int udpPort = TcpPort + 1;
                        TelemetryClient.StartTelemetry(hostIp, udpPort, LocalClientId);
                        break;

                    default:
                        Mod.LogWarning($"[ClientConnection] Unknown packet type received: {metadata}");
                        break;
                }
            }
            catch (Exception ex)
            {
                Mod.LogError($"[ClientConnection] Error processing packet '{metadata}': {ex.Message}");
            }
        }
        #endregion
    }
}