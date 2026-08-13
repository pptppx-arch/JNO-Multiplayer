namespace Assets.Scripts.Multiplayer
{
    using System;
    using System.IO;
    using System.Net.Sockets;
    using System.Text;
    using System.Threading.Tasks;

    public class NetworkSender : IDisposable
    {
        // Connects to host, sends framed payload, and returns the active TcpClient for ongoing connection management.

        public const int DefaultPort = 25555;
        public async Task<(bool success, TcpClient client)> ConnectAndSendDataAsync(string ipAddress, string data, string metadata, int port = DefaultPort)
        {
            if (string.IsNullOrEmpty(ipAddress))
            {
                Mod.LogError("[NetworkSender] Cannot send data: IP address is null or empty.");
                return (false, null);
            }

            TcpClient client = new TcpClient();

            try
            {
                var connectTask = client.ConnectAsync(ipAddress, port);
                var timeoutTask = Task.Delay(5000);

                if (await Task.WhenAny(connectTask, timeoutTask) == timeoutTask)
                {
                    Mod.LogError($"[NetworkSender] Connection timed out to {ipAddress}:{port}");
                    client.Close();
                    return (false, null);
                }

                NetworkStream stream = client.GetStream();
                byte[] packetBytes = BuildPacket(data, metadata);

                await stream.WriteAsync(packetBytes, 0, packetBytes.Length);
                await stream.FlushAsync();

                Mod.Log($"[NetworkSender] Successfully sent {packetBytes.Length} bytes to {ipAddress}:{port}");

                // Return client WITHOUT disposing so ConnectionHandler can maintain session
                return (true, client);
            }
            catch (Exception e)
            {
                Mod.LogError($"[NetworkSender] Failed to send data to {ipAddress}:{port} - {e.Message}");
                client.Close();
                return (false, null);
            }
        }

        public static byte[] BuildPacket(string payload, string metadata)
        {
            byte[] metaBytes = Encoding.UTF8.GetBytes(metadata ?? string.Empty);
            byte[] payloadBytes = Encoding.UTF8.GetBytes(payload ?? string.Empty);

            using (MemoryStream ms = new MemoryStream())
            using (BinaryWriter writer = new BinaryWriter(ms))
            {
                // Calculate and write TOTAL payload length header [4 Bytes]
                int totalContentLength = 4 + metaBytes.Length + 4 + payloadBytes.Length;
                writer.Write(totalContentLength);

                // Write Metadata length and bytes
                writer.Write(metaBytes.Length);
                if (metaBytes.Length > 0) writer.Write(metaBytes);

                // Write Payload length and bytes
                writer.Write(payloadBytes.Length);
                if (payloadBytes.Length > 0) writer.Write(payloadBytes);

                return ms.ToArray();
            }
        }

        public void Dispose() { }
    }
}