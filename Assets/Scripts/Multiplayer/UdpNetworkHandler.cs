namespace Assets.Scripts.Multiplayer.Telemetry
{
    using System;
    using System.Net;
    using System.Net.Sockets;
    using System.Text;
    using System.Threading.Tasks;

    public class UdpNetworkHandler : IDisposable
    {
        private UdpClient _udpClient;

        // Binds socket for listening (Host)
        public UdpNetworkHandler(int listenPort)
        {
            _udpClient = new UdpClient(listenPort);
        }

        // Unbound socket for sending (Client)
        public UdpNetworkHandler()
        {
            _udpClient = new UdpClient();
        }

        public async Task SendAsync(string payload, IPEndPoint targetEndPoint)
        {
            if (_udpClient == null || targetEndPoint == null) return;

            byte[] bytes = Encoding.UTF8.GetBytes(payload ?? string.Empty);
            await _udpClient.SendAsync(bytes, bytes.Length, targetEndPoint);
        }

        public async Task<(string payload, IPEndPoint remoteEP)> ReceiveAsync()
        {
            if (_udpClient == null) return (null, null);

            var result = await _udpClient.ReceiveAsync();
            string payload = Encoding.UTF8.GetString(result.Buffer);
            return (payload, result.RemoteEndPoint);
        }

        public void Close()
        {
            _udpClient?.Close();
            _udpClient = null;
        }

        public void Dispose() => Close();
    }
}