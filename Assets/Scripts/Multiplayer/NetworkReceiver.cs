namespace Assets.Scripts.Multiplayer
{
    using System;
    using System.IO;
    using System.Net.Sockets;
    using System.Text;
    using System.Threading.Tasks;

    public class NetworkReceiver : IDisposable
    {
        /// Receives framed metadata and payload from an active TCP connection.

        public async Task<(string payload, string metadata)> ReceiveDataAsync(TcpClient client)
        {
            if (client == null || !client.Connected)
            {
                Mod.LogError("[NetworkReceiver] Cannot receive data: TcpClient is null or disconnected.");
                return (null, null);
            }

            try
            {
                NetworkStream stream = client.GetStream();

                // 1. Read total packet length (4 bytes)
                byte[] lengthBuffer = new byte[4];
                if (!await ReadExactAsync(stream, lengthBuffer, 4))
                {
                    Mod.LogError("[NetworkReceiver] Failed to read packet length outer header.");
                    return (null, null);
                }

                int packetLength = BitConverter.ToInt32(lengthBuffer, 0);
                if (packetLength <= 0)
                {
                    Mod.LogError("[NetworkReceiver] Received invalid packet length.");
                    return (null, null);
                }

                // 2. Read full packet bytes
                byte[] packetBuffer = new byte[packetLength];
                if (!await ReadExactAsync(stream, packetBuffer, packetLength))
                {
                    Mod.LogError("[NetworkReceiver] Failed to read complete packet payload.");
                    return (null, null);
                }

                // 3. Unpack into metadata and payload
                return UnpackPacket(packetBuffer);
            }
            catch (Exception e)
            {
                Mod.LogError($"[NetworkReceiver] Receive error - {e.Message}");
                return (null, null);
            }
        }

        private (string payload, string metadata) UnpackPacket(byte[] buffer)
        {
            using (MemoryStream ms = new MemoryStream(buffer))
            using (BinaryReader reader = new BinaryReader(ms))
            {
                int metaLength = reader.ReadInt32();
                string metadata = metaLength > 0 ? Encoding.UTF8.GetString(reader.ReadBytes(metaLength)) : string.Empty;

                int payloadLength = reader.ReadInt32();
                string payload = payloadLength > 0 ? Encoding.UTF8.GetString(reader.ReadBytes(payloadLength)) : string.Empty;

                return (payload, metadata);
            }
        }

        // Guarantees reading the exact byte count even if TCP splits packets
        private async Task<bool> ReadExactAsync(NetworkStream stream, byte[] buffer, int count)
        {
            int totalBytesRead = 0;
            while (totalBytesRead < count)
            {
                int bytesRead = await stream.ReadAsync(buffer, totalBytesRead, count - totalBytesRead);
                if (bytesRead == 0) return false; // Remote socket closed
                totalBytesRead += bytesRead;
            }
            return true;
        }

        public void Dispose() { }
    }
}