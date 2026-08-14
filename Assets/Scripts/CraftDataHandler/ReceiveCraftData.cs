namespace Assets.Scripts.Multiplayer.CraftData
{
    using Assets.Scripts.Multiplayer.Telemetry;
    using System;
    using System.IO;
    using System.IO.Compression;
    using System.Text;
    using System.Threading.Tasks;

    public static class ReceiveCraftData
    {
        #region Decompression & Processing
        public static string DecompressCraftXml(string craftData)
        {
            if (string.IsNullOrWhiteSpace(craftData)) return string.Empty;

            string trimmed = craftData.TrimStart();
            // If payload is already raw uncompressed XML
            if (trimmed.StartsWith("<"))
            {
                return craftData;
            }

            try
            {
                byte[] compressedBytes = Convert.FromBase64String(craftData);
                using (MemoryStream ms = new MemoryStream(compressedBytes))
                using (GZipStream gzip = new GZipStream(ms, CompressionMode.Decompress))
                using (StreamReader reader = new StreamReader(gzip, Encoding.UTF8))
                {
                    return reader.ReadToEnd();
                }
            }
            catch (Exception ex)
            {
                Mod.LogError($"[ReceiveCraftData] Failed to decompress craft XML: {ex.Message}");
                return string.Empty;
            }
        }

        public static async Task ProcessAndSpawnAsync(int clientId, string craftData)
        {
            string decompressedXml = DecompressCraftXml(craftData);
            if (string.IsNullOrEmpty(decompressedXml))
            {
                Mod.LogError($"[ReceiveCraftData] Cannot spawn craft for Client ID {clientId}: Unreadable or empty XML.");
                return;
            }

            await CraftSpawner.SpawnCraft(clientId, decompressedXml);
        }
        #endregion
    }
}