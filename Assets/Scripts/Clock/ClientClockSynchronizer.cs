namespace Assets.Scripts.Clock
{
    using System;
    using System.Diagnostics;

    /// <summary>
    /// Estimates the host's monotonic tick on the client. Samples are supplied by the
    /// authenticated TCP control channel, using the midpoint of the measured round trip.
    /// The result is for visual snapshot playback only; it never changes host authority.
    /// </summary>
    public sealed class ClientClockSynchronizer
    {
        private readonly object _sync = new object();
        private readonly Stopwatch _elapsed = new Stopwatch();

        private const int DefaultTickRate = 60;
        private bool _hasEstimate;
        private double _hostTicksMinusLocalTicks;
        private int _tickRate = DefaultTickRate;
        private long _lastObservedHostTick;

        public int TickRate
        {
            get
            {
                lock (_sync)
                {
                    return _tickRate;
                }
            }
        }

        public void Start()
        {
            lock (_sync)
            {
                _elapsed.Reset();
                _elapsed.Start();
                _hasEstimate = false;
                _hostTicksMinusLocalTicks = 0.0;
                _tickRate = DefaultTickRate;
                _lastObservedHostTick = 0;
            }
        }

        public void Stop()
        {
            lock (_sync)
            {
                _elapsed.Stop();
                _hasEstimate = false;
            }
        }

        /// <summary>
        /// Uses a timestamp from Stopwatch.GetTimestamp so the server can echo it without
        /// interpreting client wall-clock time.
        /// </summary>
        public static long CreateRequestTimestamp()
        {
            return Stopwatch.GetTimestamp();
        }

        public void ObserveRoundTrip(long clientSentTimestamp, long serverTick, int serverTickRate)
        {
            if (clientSentTimestamp <= 0 || serverTick < 0 || serverTickRate <= 0)
            {
                return;
            }

            long nowTimestamp = Stopwatch.GetTimestamp();
            if (nowTimestamp < clientSentTimestamp)
            {
                return;
            }

            double roundTripSeconds =
                (nowTimestamp - clientSentTimestamp) / (double)Stopwatch.Frequency;

            lock (_sync)
            {
                if (!_elapsed.IsRunning)
                {
                    return;
                }

                _tickRate = serverTickRate;
                double localTicks = _elapsed.Elapsed.TotalSeconds * _tickRate;
                double hostTicksAtReceive = serverTick + (roundTripSeconds * _tickRate * 0.5);
                double observedOffset = hostTicksAtReceive - localTicks;

                // Keep later samples stable while allowing the estimate to adapt to normal
                // network-jitter changes. The original sample is an explicit RTT midpoint.
                _hostTicksMinusLocalTicks = _hasEstimate
                    ? (_hostTicksMinusLocalTicks * 0.8) + (observedOffset * 0.2)
                    : observedOffset;
                _hasEstimate = true;
                _lastObservedHostTick = Math.Max(_lastObservedHostTick, serverTick);
            }
        }

        /// <summary>
        /// A relayed packet is a lower-confidence host-time observation. It provides a
        /// safe fallback before a TCP clock response reaches the client.
        /// </summary>
        public void ObserveTelemetryTick(long hostTick)
        {
            if (hostTick < 0) return;

            lock (_sync)
            {
                _lastObservedHostTick = Math.Max(_lastObservedHostTick, hostTick);
                if (_hasEstimate || !_elapsed.IsRunning)
                {
                    return;
                }

                double localTicks = _elapsed.Elapsed.TotalSeconds * _tickRate;
                _hostTicksMinusLocalTicks = hostTick - localTicks;
                _hasEstimate = true;
            }
        }

        public long GetPresentationTick(int interpolationDelayTicks)
        {
            if (interpolationDelayTicks < 0) throw new ArgumentOutOfRangeException(nameof(interpolationDelayTicks));

            lock (_sync)
            {
                if (!_elapsed.IsRunning || !_hasEstimate)
                {
                    return Math.Max(0, _lastObservedHostTick - interpolationDelayTicks);
                }

                double localTicks = _elapsed.Elapsed.TotalSeconds * _tickRate;
                long estimatedHostTick = (long)Math.Floor(localTicks + _hostTicksMinusLocalTicks);
                return Math.Max(0, estimatedHostTick - interpolationDelayTicks);
            }
        }
    }
}
