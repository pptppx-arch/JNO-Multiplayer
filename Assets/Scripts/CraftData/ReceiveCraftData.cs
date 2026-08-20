namespace Assets.Scripts.Multiplayer.CraftData
{
    using Assets.Scripts.Flight.Sim;
    using Assets.Scripts.Threading;
    using System;
    using System.IO;
    using System.IO.Compression;
    using System.Text;
    using System.Threading.Tasks;

    public static class ReceiveCraftData
    {
        public static string DecompressCraftXml(string craftData)
        {
            if (string.IsNullOrWhiteSpace(craftData)) return string.Empty;

            string trimmed = craftData.TrimStart();
            if (trimmed.StartsWith("<"))
            {
                return craftData;
            }

            try
            {
                byte[] compressedBytes = Convert.FromBase64String(craftData);
                using (MemoryStream input = new MemoryStream(compressedBytes))
                using (GZipStream gzip = new GZipStream(input, CompressionMode.Decompress))
                using (StreamReader reader = new StreamReader(gzip, Encoding.UTF8))
                {
                    if (reader.ToString().Length > 8 * 1024 * 1024) return null;
                    return reader.ReadToEnd();
                }
            }
            catch (Exception ex)
            {
                Mod.LogError($"[ReceiveCraftData] Failed to decompress craft XML: {ex.Message}");
                return string.Empty;
            }
        }

        /// <summary>
        /// Decompresses plain data on the calling path, then schedules Juno craft spawning
        /// for the next MultiplayerThread.Pump() call on the game thread.
        /// </summary>
        public static Task<CraftNode> ProcessAndSpawnAsync(int clientId, string craftData)
        {
            string decompressedXml = DecompressCraftXml(craftData);
            if (string.IsNullOrEmpty(decompressedXml))
            {
                Mod.LogError($"[ReceiveCraftData] Cannot spawn craft for Client ID {clientId}: unreadable or empty XML.");
                return Task.FromResult<CraftNode>(null);
            }

            return MultiplayerThread.Enqueue(() =>
                CraftSpawner.SpawnCraftOnGameThread(clientId, decompressedXml));
        }
    }
}
