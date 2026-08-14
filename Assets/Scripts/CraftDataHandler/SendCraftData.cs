namespace Assets.Scripts.Multiplayer.CraftData
{
    using Assets.Scripts.Flight;
    using Assets.Scripts.Multiplayer;
    using System;
    using System.IO;
    using System.IO.Compression;
    using System.Net.Sockets;
    using System.Text;
    using System.Threading.Tasks;
    using System.Xml.Linq;

    public static class SendCraftData
    {
        #region Local XML Extraction & Compression
        public static string GetLocalCraftXmlCompressed()
        {
            var flightScene = FlightSceneScript.Instance;
            if (flightScene == null || flightScene.CraftNode == null)
            {
                Mod.LogWarning("[SendCraftData] FlightScene or local player CraftNode is not ready.");
                return string.Empty;
            }

            try
            {
                var nodeId = flightScene.CraftNode.NodeId;
                XElement xml = flightScene.FlightState.LoadCraftXml(nodeId);
                string rawXml = xml.ToString(SaveOptions.DisableFormatting);

                byte[] rawBytes = Encoding.UTF8.GetBytes(rawXml);
                using (MemoryStream outputStream = new MemoryStream())
                {
                    using (GZipStream gzipStream = new GZipStream(outputStream, CompressionMode.Compress))
                    {
                        gzipStream.Write(rawBytes, 0, rawBytes.Length);
                    }
                    return Convert.ToBase64String(outputStream.ToArray());
                }
            }
            catch (Exception ex)
            {
                Mod.LogError($"[SendCraftData] Error capturing and compressing local craft XML: {ex.Message}");
                return string.Empty;
            }
        }
        #endregion


        #region Transmission Routines
        public static async Task SendLocalCraftAsync(TcpClient client, string metadata = "CLIENT_CRAFT_DATA")
        {
            if (client == null || !client.Connected)
            {
                Mod.LogError("[SendCraftData] Cannot send local craft: Target socket is null or disconnected.");
                return;
            }

            string compressedXml = GetLocalCraftXmlCompressed();
            if (string.IsNullOrEmpty(compressedXml)) return;

            await SendRawPayloadAsync(client, compressedXml, metadata);
        }

        public static async Task SendRawPayloadAsync(TcpClient client, string payload, string metadata)
        {
            if (client == null || !client.Connected)
            {
                Mod.LogError("[SendCraftData] Cannot send payload: Target socket is null or disconnected.");
                return;
            }

            try
            {
                byte[] packetBytes = NetworkSender.BuildPacket(payload, metadata);
                NetworkStream stream = client.GetStream();
                await stream.WriteAsync(packetBytes, 0, packetBytes.Length);
                await stream.FlushAsync();

                Mod.Log($"[SendCraftData] Sent craft packet '{metadata}' to target client.");
            }
            catch (Exception ex)
            {
                Mod.LogError($"[SendCraftData] Error transmitting packet '{metadata}': {ex.Message}");
            }
        }
        #endregion
    }
}