namespace Assets.Scripts.Multiplayer.CraftData
{
    using Assets.Scripts.Flight;
    using Assets.Scripts.Multiplayer;
    using System;
    using System.IO;
    using System.IO.Compression;
    using System.Security.Cryptography;
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
            return TryGetLocalCraftXmlCompressedAndHashOnGameThread(
                out string compressedXml,
                out string ignoredContentHash)
                ? compressedXml
                : string.Empty;
        }

        /// <summary>
        /// Reads the active Juno flight craft once, produces the bounded wire payload, and
        /// returns a SHA-256 hash of the uncompressed XML. The hash is local-only: it is used
        /// to avoid sending a new craft payload when the craft XML has not changed.
        /// </summary>
        public static bool TryGetLocalCraftXmlCompressedAndHashOnGameThread(
            out string compressedXml,
            out string contentHash)
        {
            compressedXml = string.Empty;
            contentHash = string.Empty;

            FlightSceneScript flightScene = FlightSceneScript.Instance;
            if (flightScene == null || flightScene.CraftNode == null)
            {
                return false;
            }

            try
            {
                var nodeId = flightScene.CraftNode.NodeId;
                XElement xml = flightScene.FlightState.LoadCraftXml(nodeId);
                if (xml == null)
                {
                    Mod.LogWarning("[SendCraftData] Juno returned no XML for the local craft.");
                    return false;
                }

                string rawXml = xml.ToString(SaveOptions.DisableFormatting);
                byte[] rawBytes = Encoding.UTF8.GetBytes(rawXml);
                if (rawBytes.Length > ReceiveCraftData.MaximumCraftXmlBytes)
                {
                    Mod.LogWarning($"[SendCraftData] Local craft XML is {rawBytes.Length} bytes; maximum is {ReceiveCraftData.MaximumCraftXmlBytes} bytes.");
                    return false;
                }

                using (SHA256 sha256 = SHA256.Create())
                {
                    contentHash = Convert.ToBase64String(sha256.ComputeHash(rawBytes));
                }

                using (MemoryStream output = new MemoryStream())
                {
                    using (GZipStream gzip = new GZipStream(output, CompressionMode.Compress))
                    {
                        gzip.Write(rawBytes, 0, rawBytes.Length);
                    }

                    compressedXml = Convert.ToBase64String(output.ToArray());
                    int wireBytes = Encoding.UTF8.GetByteCount(compressedXml);
                    if (wireBytes > TcpNetworkReceiver.MaximumPayloadBytes)
                    {
                        Mod.LogWarning($"[SendCraftData] Compressed craft XML is {wireBytes} bytes; maximum TCP payload is {TcpNetworkReceiver.MaximumPayloadBytes} bytes.");
                        compressedXml = string.Empty;
                        contentHash = string.Empty;
                        return false;
                    }
                }

                return true;
            }
            catch (Exception ex)
            {
                Mod.LogError($"[SendCraftData] Error capturing and compressing local craft XML: {ex.Message}");
                compressedXml = string.Empty;
                contentHash = string.Empty;
                return false;
            }
        }
    }
}