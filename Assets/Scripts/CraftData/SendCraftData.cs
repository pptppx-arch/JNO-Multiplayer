namespace Assets.Scripts.Multiplayer.CraftData
{
    using Assets.Scripts.Flight;
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
    }
}
