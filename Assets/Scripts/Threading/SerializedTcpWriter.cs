namespace Assets.Scripts.Threading
{
    using Assets.Scripts.Multiplayer;
    using System;
    using System.Collections.Concurrent;
    using System.Net.Sockets;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// The only component permitted to write frames to one TCP connection.
    /// All producers enqueue complete frames; this class writes them in FIFO order.
    /// </summary>
    public sealed class SerializedTcpWriter : IDisposable
    {
        private readonly TcpClient _client;
        private readonly string _ownerName;
        private readonly ConcurrentQueue<byte[]> _outboundFrames =
            new ConcurrentQueue<byte[]>();
        private readonly SemaphoreSlim _outboundSignal = new SemaphoreSlim(0);
        private readonly CancellationTokenSource _cancellation =
            new CancellationTokenSource();

        private Task _writerTask;
        private bool _closed;

        public SerializedTcpWriter(TcpClient client, string ownerName)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
            _ownerName = string.IsNullOrEmpty(ownerName) ? "TCP peer" : ownerName;
        }

        public void Start()
        {
            if (_closed || _writerTask != null) return;
            _writerTask = WriteLoopAsync();
        }

        public bool Enqueue(string data, string metadata)
        {
            if (_closed || !_client.Connected || string.IsNullOrEmpty(metadata))
            {
                return false;
            }

            _outboundFrames.Enqueue(
                NetworkSender.BuildPacket(data ?? string.Empty, metadata));
            _outboundSignal.Release();
            return true;
        }

        private async Task WriteLoopAsync()
        {
            try
            {
                NetworkStream stream = _client.GetStream();

                while (!_cancellation.IsCancellationRequested)
                {
                    await _outboundSignal.WaitAsync(_cancellation.Token);

                    while (_outboundFrames.TryDequeue(out byte[] frame))
                    {
                        await stream.WriteAsync(frame, 0, frame.Length);
                        await stream.FlushAsync();
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Expected during Dispose().
            }
            catch (Exception ex)
            {
                Mod.LogWarning($"[TCP] Writer for {_ownerName} stopped: {ex.Message}");
            }
        }

        public void Dispose()
        {
            if (_closed) return;
            _closed = true;

            _cancellation.Cancel();
            _outboundSignal.Release();

            try
            {
                _client.Close();
            }
            catch
            {
                // Socket closure is best-effort during teardown.
            }

            _cancellation.Dispose();
            _outboundSignal.Dispose();
        }
    }
}