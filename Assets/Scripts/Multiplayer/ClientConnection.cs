namespace Assets.Scripts.Multiplayer
{
    using Assets.Scripts.Flight;
    using System;
    using System.IO;
    using System.IO.Compression;
    using System.Net.Sockets;
    using System.Threading.Tasks;
    using System.Xml.Linq;

    public static class ClientConnection
    {
        public static TcpClient ActiveClient { get; private set; }
        public static int LocalClientId { get; private set; }

        //Initiates a connection to the server and starts listening for incoming data.
        public static async void Connect(string host, int port)
        {
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
                Mod.LogError("[ClientConnection] Failed to connect.");
            }
        }

        //Listens for incoming data from the server.
        private static async Task StartListeningAsync(TcpClient client)
        {
            try
            {
                using (var receiver = new NetworkReceiver())
                {
                    while (client.Connected)
                    {
                        var (data, metadata) = await receiver.ReceiveDataAsync(client);
                        if (data == null || metadata == null) break;

                        switch (metadata)
                        {
                            case "CONNECT_ACCEPTED":
                                if (int.TryParse(data, out int assignedId))
                                {
                                    LocalClientId = assignedId;
                                    Mod.Log($"[ClientConnection] Connection accepted, assigned Client ID: {LocalClientId}");
                                    await SendCraftData();
                                }
                                else
                                {
                                    Mod.LogError("[ClientConnection] Failed to parse assigned Client ID. Disconnecting from server now.");
                                    client.Close();
                                }
                                break;

                            case "UPDATE_CRAFT_DATA":
                                UpdateCraftData();
                                break;

                            default:
                                Mod.LogWarning($"[ClientConnection] Received unhandled packet type: '{metadata}'");
                                break;
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


        #region Deal with lots of stuff
        public static async Task SendCraftData()
        {
            var flightScene = FlightSceneScript.Instance;
            var nodeId = flightScene.CraftNode.NodeId;
            XElement xml = flightScene.FlightState.LoadCraftXml(nodeId);
            string craftData = xml.ToString(SaveOptions.DisableFormatting);

            if (ActiveClient == null || !ActiveClient.Connected)
            {
                Mod.LogError("[ClientConnection] Cannot send craft data: ActiveClient is null or disconnected.");
                return;
            }

            try
            {
                byte[] rawBytes = System.Text.Encoding.UTF8.GetBytes(craftData);
                byte[] compressedBytes;

                using (MemoryStream outputStream = new MemoryStream())
                {
                    using (GZipStream gzipStream = new GZipStream(outputStream, CompressionMode.Compress))
                    {
                        gzipStream.Write(rawBytes, 0, rawBytes.Length);
                    }
                    compressedBytes = outputStream.ToArray();
                }

                string base64Craft = Convert.ToBase64String(compressedBytes);
                byte[] packetBytes = NetworkSender.BuildPacket(base64Craft, "CLIENT_CRAFT_DATA");

                NetworkStream stream = ActiveClient.GetStream();
                await stream.WriteAsync(packetBytes, 0, packetBytes.Length);
                await stream.FlushAsync();

                Mod.Log("[ClientConnection] Craft XML data sent to host.");
            }
            catch (Exception ex)
            {
                Mod.LogError($"[ClientConnection] Failed to send craft data: {ex.Message}");
            }
        }
        public static async void UpdateCraftData()
        {

        }
        #endregion
    }
}