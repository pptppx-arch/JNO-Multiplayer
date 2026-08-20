namespace Assets.Scripts.Multiplayer
{
    using System;
    using System.IO;
    using System.Net.Sockets;
    using System.Text;
    using System.Threading.Tasks;

    /// <summary>
    /// Shared length-prefixed TCP frame receiver. Every length is validated before an
    /// allocation, byte read, or UTF-8 decode. These limits protect both host and client.
    /// </summary>
    public class TcpNetworkReceiver : IDisposable
    {
        // Wire-frame maxima. The largest permitted payload is deliberately below the
        // outer-frame cap so the two 4-byte inner length fields and metadata also fit.
        public const int MaximumFrameBytes = 2 * 1024 * 1024;
        public const int MaximumMetadataBytes = 256;
        public const int MaximumPayloadBytes = MaximumFrameBytes - 8 - MaximumMetadataBytes;

        private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);

        /// <summary>
        /// Receives one complete frame: [outerLength][metadataLength][metadata]
        /// [dataLength][data]. Returns null values for a malformed, oversized, or closed
        /// connection; callers should end that TCP session rather than continue parsing.
        /// </summary>
        public async Task<(string data, string metadata)> ReceiveDataAsync(TcpClient client)
        {
            if (client == null || !client.Connected)
            {
                Mod.LogError("[NetworkReceiver] Cannot receive data: TcpClient is null or disconnected.");
                return (null, null);
            }

            try
            {
                NetworkStream stream = client.GetStream();

                byte[] lengthBuffer = new byte[sizeof(int)];
                if (!await ReadExactAsync(stream, lengthBuffer, lengthBuffer.Length))
                {
                    Mod.LogError("[NetworkReceiver] Failed to read outer frame length.");
                    return (null, null);
                }

                int packetLength = BitConverter.ToInt32(lengthBuffer, 0);
                if (packetLength < sizeof(int) * 2 || packetLength > MaximumFrameBytes)
                {
                    Mod.LogWarning($"[NetworkReceiver] Rejected TCP frame length {packetLength}; allowed range is 8 to {MaximumFrameBytes} bytes.");
                    return (null, null);
                }

                // Allocation occurs only after the outer length has passed its cap.
                byte[] packetBuffer = new byte[packetLength];
                if (!await ReadExactAsync(stream, packetBuffer, packetBuffer.Length))
                {
                    Mod.LogError("[NetworkReceiver] Failed to read complete TCP frame payload.");
                    return (null, null);
                }

                return TryUnpackPacket(packetBuffer, out string data, out string metadata)
                    ? (data, metadata)
                    : (null, null);
            }
            catch (Exception exception)
            {
                Mod.LogError($"[NetworkReceiver] Receive error: {exception.Message}");
                return (null, null);
            }
        }

        private static bool TryUnpackPacket(byte[] buffer, out string data, out string metadata)
        {
            data = null;
            metadata = null;
            if (buffer == null || buffer.Length < sizeof(int) * 2 || buffer.Length > MaximumFrameBytes)
            {
                return false;
            }

            try
            {
                int offset = 0;
                if (!TryReadInt32(buffer, ref offset, out int metadataLength)
                    || metadataLength < 0
                    || metadataLength > MaximumMetadataBytes
                    || metadataLength > buffer.Length - offset)
                {
                    Mod.LogWarning("[NetworkReceiver] Rejected invalid TCP metadata length.");
                    return false;
                }

                metadata = DecodeUtf8(buffer, offset, metadataLength);
                offset += metadataLength;

                if (!TryReadInt32(buffer, ref offset, out int dataLength)
                    || dataLength < 0
                    || dataLength > MaximumPayloadBytes
                    || dataLength > buffer.Length - offset)
                {
                    Mod.LogWarning("[NetworkReceiver] Rejected invalid TCP payload length.");
                    return false;
                }

                // A frame must contain exactly the declared metadata and data bytes.
                if (offset + dataLength != buffer.Length)
                {
                    Mod.LogWarning("[NetworkReceiver] Rejected TCP frame with inconsistent inner lengths.");
                    return false;
                }

                data = DecodeUtf8(buffer, offset, dataLength);
                return true;
            }
            catch (DecoderFallbackException)
            {
                Mod.LogWarning("[NetworkReceiver] Rejected TCP frame containing invalid UTF-8.");
                data = null;
                metadata = null;
                return false;
            }
            catch (Exception exception)
            {
                Mod.LogWarning($"[NetworkReceiver] Rejected malformed TCP frame: {exception.Message}");
                data = null;
                metadata = null;
                return false;
            }
        }

        private static bool TryReadInt32(byte[] buffer, ref int offset, out int value)
        {
            value = 0;
            if (offset < 0 || buffer == null || buffer.Length - offset < sizeof(int))
            {
                return false;
            }

            value = BitConverter.ToInt32(buffer, offset);
            offset += sizeof(int);
            return true;
        }

        private static string DecodeUtf8(byte[] buffer, int offset, int count)
        {
            return count == 0 ? string.Empty : StrictUtf8.GetString(buffer, offset, count);
        }

        // Guarantees reading the exact byte count even if TCP splits packets.
        private static async Task<bool> ReadExactAsync(NetworkStream stream, byte[] buffer, int count)
        {
            int totalBytesRead = 0;
            while (totalBytesRead < count)
            {
                int bytesRead = await stream.ReadAsync(buffer, totalBytesRead, count - totalBytesRead);
                if (bytesRead == 0) return false;
                totalBytesRead += bytesRead;
            }

            return true;
        }

        public void Dispose() { }
    }
}
