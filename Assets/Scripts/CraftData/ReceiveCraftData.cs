namespace Assets.Scripts.Multiplayer.CraftData
{
    using Assets.Scripts.Flight.Sim;
    using Assets.Scripts.Threading;
    using System;
    using System.IO;
    using System.IO.Compression;
    using System.Text;
    using System.Threading.Tasks;

    /// <summary>
    /// Validates, decompresses, and dispatches received craft XML. TCP framing limits the
    /// wire payload; this class separately bounds Base64 decoding and GZip expansion.
    /// </summary>
    public static class ReceiveCraftData
    {
        // The wire value is a UTF-8 TCP payload. The decompressed XML cap protects against
        // GZip expansion while still allowing a substantially larger valid craft document.
        public const int MaximumCraftWireBytes = TcpNetworkReceiver.MaximumPayloadBytes;
        public const int MaximumCraftXmlBytes = 8 * 1024 * 1024;

        private const int DecompressionBufferBytes = 16 * 1024;
        private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);

        public static string DecompressCraftXml(string craftData)
        {
            if (string.IsNullOrWhiteSpace(craftData)) return string.Empty;

            // This protects direct callers as well as the normal bounded TCP receive path.
            if (GetUtf8ByteCountWithinLimit(craftData, MaximumCraftWireBytes) < 0)
            {
                Mod.LogWarning($"[ReceiveCraftData] Rejected craft wire payload above {MaximumCraftWireBytes} bytes.");
                return string.Empty;
            }

            string trimmed = craftData.TrimStart();
            if (trimmed.StartsWith("<"))
            {
                // Raw XML is accepted only within the same wire cap.
                return craftData;
            }

            try
            {
                byte[] compressedBytes = Convert.FromBase64String(craftData);
                if (compressedBytes.Length > MaximumCraftWireBytes)
                {
                    Mod.LogWarning($"[ReceiveCraftData] Rejected decoded compressed craft data above {MaximumCraftWireBytes} bytes.");
                    return string.Empty;
                }

                using (MemoryStream input = new MemoryStream(compressedBytes, writable: false))
                using (GZipStream gzip = new GZipStream(input, CompressionMode.Decompress))
                {
                    return ReadGzipXmlWithLimit(gzip);
                }
            }
            catch (FormatException)
            {
                Mod.LogWarning("[ReceiveCraftData] Rejected craft data with invalid Base64 encoding.");
                return string.Empty;
            }
            catch (InvalidDataException)
            {
                Mod.LogWarning("[ReceiveCraftData] Rejected craft data with invalid GZip content.");
                return string.Empty;
            }
            catch (DecoderFallbackException)
            {
                Mod.LogWarning("[ReceiveCraftData] Rejected craft XML with invalid UTF-8.");
                return string.Empty;
            }
            catch (Exception exception)
            {
                Mod.LogError($"[ReceiveCraftData] Failed to decompress craft XML: {exception.Message}");
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
                Mod.LogError($"[ReceiveCraftData] Cannot spawn craft for Client ID {clientId}: unreadable, empty, or oversized XML.");
                return Task.FromResult<CraftNode>(null);
            }

            return MultiplayerThread.Enqueue(() =>
                CraftSpawner.SpawnCraftOnGameThread(clientId, decompressedXml));
        }

        private static string ReadGzipXmlWithLimit(Stream gzip)
        {
            byte[] buffer = new byte[DecompressionBufferBytes];
            int totalBytes = 0;

            using (MemoryStream output = new MemoryStream())
            {
                while (true)
                {
                    int bytesRead = gzip.Read(buffer, 0, buffer.Length);
                    if (bytesRead == 0) break;

                    if (bytesRead > MaximumCraftXmlBytes - totalBytes)
                    {
                        Mod.LogWarning($"[ReceiveCraftData] Rejected GZip craft XML above {MaximumCraftXmlBytes} bytes after decompression.");
                        return string.Empty;
                    }

                    output.Write(buffer, 0, bytesRead);
                    totalBytes += bytesRead;
                }

                if (totalBytes == 0)
                {
                    return string.Empty;
                }

                return StrictUtf8.GetString(output.ToArray());
            }
        }

        // Validates the UTF-8 byte count against the configured maximum and rejects
        // invalid UTF-16 surrogate sequences before any XML parsing is attempted.
        private static int GetUtf8ByteCountWithinLimit(string value, int maximumBytes)
        {
            try
            {
                int byteCount = StrictUtf8.GetByteCount(value);
                return byteCount <= maximumBytes ? byteCount : -1;
            }
            catch (EncoderFallbackException)
            {
                return -1;
            }
        }
    }
}
