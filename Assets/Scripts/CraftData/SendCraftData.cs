namespace Assets.Scripts.Multiplayer.CraftData
{
    using Assets.Scripts.Flight;
    using Assets.Scripts.Multiplayer;
    using System;
    using System.IO;
    using System.IO.Compression;
    using System.Text;
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
                if (rawBytes.Length > ReceiveCraftData.MaximumCraftXmlBytes)
                {
                    Mod.LogWarning($"[SendCraftData] Local craft XML is {rawBytes.Length} bytes; maximum is {ReceiveCraftData.MaximumCraftXmlBytes} bytes.");
                    return string.Empty;
                }
                using (MemoryStream output = new MemoryStream())
                {
                    using (GZipStream gzip = new GZipStream(output, CompressionMode.Compress))
                    {
                        gzip.Write(rawBytes, 0, rawBytes.Length);
                    }

                    string compressedXml = Convert.ToBase64String(output.ToArray());
                    int wireBytes = Encoding.UTF8.GetByteCount(compressedXml);
                    if (wireBytes > TcpNetworkReceiver.MaximumPayloadBytes)
                    {
                        Mod.LogWarning($"[SendCraftData] Compressed craft XML is {wireBytes} bytes; maximum TCP payload is {TcpNetworkReceiver.MaximumPayloadBytes} bytes.");
                        return string.Empty;
                    }

                    return compressedXml;
                }
            }
            catch (Exception ex)
            {
                Mod.LogError($"[SendCraftData] Error capturing and compressing local craft XML: {ex.Message}");
                return string.Empty;
            }
        }
    }
}
