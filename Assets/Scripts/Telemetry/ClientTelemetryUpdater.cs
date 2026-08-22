namespace Assets.Scripts.Multiplayer.Telemetry
{
    using Assets.Scripts.Flight;
    using Assets.Scripts.Flight.Sim;
    using Assets.Scripts.Clock;
    using System;
    using System.Diagnostics;
    using System.Net;

    /// <summary>
    /// One per connected client. Sends only the local craft's telemetry to the host and
    /// applies host-relayed packets to registered remote proxy crafts.
    /// </summary>
    public sealed class ClientTelemetryUpdater : IDisposable
    {
        private readonly UdpNetworkHandler _udp;
        private readonly TelemetryReceiver _receiver;
        private readonly LocalTelemetryPackager _packager;
        private readonly IPEndPoint _hostEndPoint;
        private readonly Stopwatch _clock = new Stopwatch();
        private readonly double _sendIntervalSeconds;
        private readonly int _localClientId;

        private double _nextSendTimeSeconds;
        private bool _firstPacketPending;
        private bool _started;
        private readonly string _udpSessionToken;
        private readonly ClientClockSynchronizer _clockSynchronizer;

        public ClientTelemetryUpdater(
            IPEndPoint hostEndPoint,
            int localClientId,
            string udpSessionToken,
            ClientClockSynchronizer clockSynchronizer,
            double sendRateHz = 20.0)
        {
            if (hostEndPoint == null) throw new ArgumentNullException(nameof(hostEndPoint));
            if (localClientId < 0) throw new ArgumentOutOfRangeException(nameof(localClientId));
            if (!TelemetryPacket.IsValidSessionToken(udpSessionToken))
            {
                throw new ArgumentException("A valid UDP session token is required.", nameof(udpSessionToken));
            }

            _hostEndPoint = hostEndPoint;
            _localClientId = localClientId;
            _udpSessionToken = udpSessionToken;
            _clockSynchronizer = clockSynchronizer;
            _sendIntervalSeconds = 1.0 / sendRateHz;
            _udp = new UdpNetworkHandler();
            _receiver = new TelemetryReceiver(_udp);
            _packager = new LocalTelemetryPackager(localClientId);
        }

        public long LastObservedHostTick => _receiver.LastObservedHostTick;

        public void Start()
        {
            if (_started) return;
            _started = true;
            _clock.Start();
            _receiver.Start();

            // The first craft sample is deferred to Pump(), which runs on the Juno game
            // thread. It registers the client's ephemeral UDP endpoint with the host.
            _firstPacketPending = true;
            _nextSendTimeSeconds = 0.0;
        }

        /// <summary>
        /// Call every rendered frame from a persistent game-loop component, on the same
        /// thread that owns Juno/Unity craft objects.
        /// </summary>
        public void Pump(float deltaTime)
        {
            if (!_started) return;

            _receiver.PumpRemoteProxies(
                _localClientId,
                deltaTime,
                _hostEndPoint,
                _udpSessionToken,
                _clockSynchronizer);

            double now = _clock.Elapsed.TotalSeconds;
            if (_firstPacketPending || now >= _nextSendTimeSeconds)
            {
                _firstPacketPending = false;
                _nextSendTimeSeconds = now + _sendIntervalSeconds;
                SendLocalStateNow();
            }
        }

        public void Dispose()
        {
            _started = false;
            _firstPacketPending = false;
            _receiver.Stop();
            _udp.Close();
            _clock.Stop();
        }

        private void SendLocalStateNow()
        {
            CraftNode localCraft = FlightSceneScript.Instance == null
                ? null
                : FlightSceneScript.Instance.CraftNode as CraftNode;
            if (localCraft == null) return;

            if (_packager.TryPackage(localCraft, _receiver.LastObservedHostTick, out TelemetryPacket packet))
            {
                packet.SessionToken = _udpSessionToken;
                _ = _udp.SendAsync(packet.Serialize(), _hostEndPoint);
            }
        }
    }
}
