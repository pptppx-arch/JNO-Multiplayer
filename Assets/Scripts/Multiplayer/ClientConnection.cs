namespace Assets.Scripts.Multiplayer
{
    using System;
    using System.Net.Sockets;
    using System.Threading.Tasks;

    public static class ClientConnection
    {
        public static TcpClient ActiveClient { get; private set; }

        public static async void Connect(string host, int port, string clientCraftXml)
        {
            var networkSender = new NetworkSender();
            var (sentSuccess, client) = await networkSender.ConnectAndSendDataAsync(host, clientCraftXml, "JOIN_CLIENT_CRAFT", port);

            if (sentSuccess && client != null)
            {
                ActiveClient = client;
                Mod.Log("[ClientConnection] Connected! Starting listening loop...");
                _ = StartListeningAsync(ActiveClient);
            }
            else
            {
                Mod.LogError("[ClientConnection] Failed to connect.");
            }
        }

        private static async Task StartListeningAsync(TcpClient client)
        {
            try
            {
                using (var receiver = new NetworkReceiver())
                {
                    while (client.Connected)
                    {
                        var (payload, metadata) = await receiver.ReceiveDataAsync(client);
                        if (payload == null && metadata == null) break;

                        if (metadata == "SYNC_WORLD_CRAFTS")
                        {
                            Mod.Log("[ClientConnection] Received world craft bundle from host!");
                            // Trigger spawning logic
                        }
                        else
                        {
                            ProcessServerPacket(metadata, payload);
                        }
                    }
                }
            }
            finally
            {
                client.Close();
                ActiveClient = null;
            }
        }

        private static void ProcessServerPacket(string packetType, string payload)
        {
            // Process updates coming from Host
        }
    }
}