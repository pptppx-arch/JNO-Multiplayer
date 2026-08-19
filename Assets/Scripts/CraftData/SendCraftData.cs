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
        /// <summary>
        /// Reads the active Juno flight craft and produces compressed XML.
        /// Call only from MultiplayerTelemetryRuntime.Update() or work executed by
        /// MultiplayerThread.Pump().
        /// </summary>
        public static string GetLocalCraftXmlCompressedOnGameThread()
        {
            FlightSceneScript flightScene = FlightSceneScript.Instance;
            if (flightScene == null || flightScene.CraftNode == null)
            {
                return string.Empty;
            }

            try
            {
                var nodeId = flightScene.CraftNode.NodeId;
                XElement xml = flightScene.FlightState.LoadCraftXml(nodeId);
                string rawXml = xml.ToString(SaveOptions.DisableFormatting);

                byte[] rawBytes = Encoding.UTF8.GetBytes(rawXml);
                using (MemoryStream output = new MemoryStream())
                {
                    using (GZipStream gzip = new GZipStream(output, CompressionMode.Compress))
                    {
                        gzip.Write(rawBytes, 0, rawBytes.Length);
                    }

                    return Convert.ToBase64String(output.ToArray());
                }
            }
            catch (Exception ex)
            {
                Mod.LogError($"[SendCraftData] Error capturing and compressing local craft XML: {ex.Message}");
                return string.Empty;
            }
        }

        /// <summary>
        /// Sends an already-created payload. This method does not access Juno/Unity state.
        /// </summary>
        public static async Task SendRawPayloadAsync(TcpClient client, string payload, string metadata)
        {
            if (client == null || !client.Connected)
            {
                Mod.LogError("[SendCraftData] Cannot send payload: target socket is null or disconnected.");
                return;
            }

            if (string.IsNullOrEmpty(metadata))
            {
                Mod.LogError("[SendCraftData] Cannot send payload without metadata.");
                return;
            }

            try
            {
                byte[] packetBytes = NetworkSender.BuildPacket(payload ?? string.Empty, metadata);
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
    }
}
