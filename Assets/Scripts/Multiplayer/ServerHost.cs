namespace Assets.Scripts.Multiplayer
{
    using System;
    using System.Collections.Generic;
    using System.Net;
    using System.Net.Sockets;
    using System.Threading.Tasks;
    using System.Xml.Linq;

    public static class ServerHost
    {
        private static TcpListener _listener;
        private static bool _isHosting;

        public static List<TcpClient> ConnectedClients { get; } = new List<TcpClient>();
        public static List<string> SessionCraftXmls { get; } = new List<string>();

        public static async void Start(int port, string hostCraftXml)
        {
            try
            {
                if (!string.IsNullOrEmpty(hostCraftXml))
                {
                    lock (SessionCraftXmls) { SessionCraftXmls.Add(hostCraftXml); }
                }

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
                if (_isHosting) Mod.LogError($"[ServerHost] Error: {ex.Message}");
            }
        }

        private static async Task HandleClientAsync(TcpClient client)
        {
            lock (ConnectedClients) { ConnectedClients.Add(client); }

            try
            {
                using (var receiver = new NetworkReceiver())
                {
                    // Handshake
                    var (payload, metadata) = await receiver.ReceiveDataAsync(client);
                    if (metadata == "JOIN_CLIENT_CRAFT" && !string.IsNullOrEmpty(payload))
                    {
                        Mod.Log("[ServerHost] Client craft received.");
                        lock (SessionCraftXmls) { SessionCraftXmls.Add(payload); }

                        // Send back full world state
                        string craftBundleXml = BuildCraftBundleXml();
                        byte[] responsePacket = NetworkSender.BuildPacket(craftBundleXml, "SYNC_WORLD_CRAFTS");
                        await client.GetStream().WriteAsync(responsePacket, 0, responsePacket.Length);
                    }

                    // Loop
                    while (client.Connected)
                    {
                        var (dataPayload, packetType) = await receiver.ReceiveDataAsync(client);
                        if (dataPayload == null && packetType == null) break;

                        ProcessHostPacket(packetType, dataPayload);
                    }
                }
            }
            finally
            {
                lock (ConnectedClients) { ConnectedClients.Remove(client); }
                client.Close();
            }
        }

        private static void ProcessHostPacket(string packetType, string payload)
        {
            // Process incoming client updates (e.g., telemetry, staging)
        }

        private static string BuildCraftBundleXml()
        {
            var root = new System.Xml.Linq.XElement("CraftCollection");
            lock (SessionCraftXmls)
            {
                foreach (var xml in SessionCraftXmls)
                {
                    try { root.Add(System.Xml.Linq.XElement.Parse(xml)); } catch { }
                }
            }
            return root.ToString(SaveOptions.DisableFormatting);
        }
    }
}