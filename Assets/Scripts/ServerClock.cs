namespace Assets.Scripts.Clock
{
    using System;
    using System.Diagnostics;
    /// <summary>
    /// Host-authoritative, monotonic clock for the multiplayer simulation.
    /// 
    /// The host advances time from Stopwatch rather than DateTime so simulation time
    /// cannot jump when the operating-system clock is adjusted. Clients should receive
    /// ServerClockSnapshot values and synchronize their local fixed-step clocks to this
    /// clock's tick sequence.
    /// </summary>
    public sealed class ServerClock
    {
        private readonly object _sync = new object();
        private readonly Stopwatch _stopwatch = new Stopwatch();
        private readonly long _initialTick;
        private readonly long _epochUnixMilliseconds;

        // Only the simulation loop should consume ticks. It is protected so diagnostics or networking threads can safely read snapshots at the same time.
        private long _lastConsumedTick;
        private bool _started;

        // Creates a server clock. Tick rate must be the fixed simulation rate used by every host and client participating in the session.
        public ServerClock(int tickRate, long initialTick = 0)
        {
            if (tickRate <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(tickRate), "Tick rate must be greater than zero.");
            }

            TickRate = tickRate;
            _initialTick = initialTick;
            _lastConsumedTick = initialTick - 1;
            _epochUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        }

        // Fixed simulation updates per second.
        public int TickRate { get; }

        // Duration of one simulation step, in seconds.
        public double TickDurationSeconds => 1.0 / TickRate;

        // UTC epoch recorded when this clock was constructed. It is useful for logging and diagnostics, but it is not used to advance simulation time.
        public long EpochUnixMilliseconds => _epochUnixMilliseconds;

        // Starts the monotonic simulation clock. Calling Start more than once is safe.
        public void Start()
        {
            lock (_sync)
            {
                if (_started)
                {
                    return;
                }

                _stopwatch.Start();
                _started = true;
            }
        }

        // Stops the clock. Use only while pausing or shutting down the host; clients should not treat a paused server as a new tick epoch.
        public void Stop()
        {
            lock (_sync)
            {
                if (!_started)
                {
                    return;
                }

                _stopwatch.Stop();
                _started = false;
            }
        }

        // Returns the current authoritative tick without consuming it.
        public long CurrentTick
        {
            get
            {
                lock (_sync)
                {
                    EnsureStarted();
                    return GetTickAtElapsedStopwatchTicks(_stopwatch.ElapsedTicks);
                }
            }
        }

        // Returns continuous server simulation time in seconds. Use CurrentTick for authoritative ordering and this value only for diagnostics or presentation.
        public double GlobalTimeSeconds
        {
            get
            {
                lock (_sync)
                {
                    EnsureStarted();
                    return (_initialTick / (double)TickRate) +
                           (_stopwatch.ElapsedTicks / (double)Stopwatch.Frequency);
                }
            }
        }

        // Returns the number of fixed ticks currently due, capped to avoid an unbounded catch-up loop after a stall.
        // The caller still consumes ticks one at a time with TryConsumeNextTick.
        public int GetDueTickCount(int maximumTicks)
        {
            if (maximumTicks <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maximumTicks), "Maximum ticks must be greater than zero.");
            }

            lock (_sync)
            {
                EnsureStarted();

                long due = GetTickAtElapsedStopwatchTicks(_stopwatch.ElapsedTicks) - _lastConsumedTick;
                if (due <= 0)
                {
                    return 0;
                }

                return due > maximumTicks ? maximumTicks : (int)due;
            }
        }

        // Consumes the next due fixed simulation tick. This method is intended to be called by one host simulation loop only.
        // It returns false when the host has not yet reached the next tick boundary.
        public bool TryConsumeNextTick(out long tick)
        {
            lock (_sync)
            {
                EnsureStarted();

                long currentTick = GetTickAtElapsedStopwatchTicks(_stopwatch.ElapsedTicks);
                if (_lastConsumedTick >= currentTick)
                {
                    tick = 0;
                    return false;
                }

                _lastConsumedTick++;
                tick = _lastConsumedTick;
                return true;
            }
        }

        // Creates one atomic-enough network sample from a single stopwatch reading.
        // Include this snapshot, or at least its Tick and TickRate, in host telemetry and in explicit client clock-sync messages.=
        public ServerClockSnapshot GetSnapshot()
        {
            lock (_sync)
            {
                EnsureStarted();

                long elapsedStopwatchTicks = _stopwatch.ElapsedTicks;
                return new ServerClockSnapshot(
                    GetTickAtElapsedStopwatchTicks(elapsedStopwatchTicks),
                    TickRate,
                    _epochUnixMilliseconds,
                    (_initialTick / (double)TickRate) +
                    (elapsedStopwatchTicks / (double)Stopwatch.Frequency));
            }
        }

        private long GetTickAtElapsedStopwatchTicks(long elapsedStopwatchTicks)
        {
            // Avoid multiplying a long-running elapsed timestamp by TickRate directly.
            // Splitting into seconds and a remainder keeps the calculation stable.
            long wholeSeconds = elapsedStopwatchTicks / Stopwatch.Frequency;
            long remainingStopwatchTicks = elapsedStopwatchTicks % Stopwatch.Frequency;

            return _initialTick +
                   (wholeSeconds * TickRate) +
                   ((remainingStopwatchTicks * TickRate) / Stopwatch.Frequency);
        }

        private void EnsureStarted()
        {
            if (!_started)
            {
                throw new InvalidOperationException("ServerClock must be started before it is read or consumed.");
            }
        }
    }
    
    // Compact host-clock sample intended for connection setup, clock synchronization, telemetry packets, and collision proposals/resolutions.
    public struct ServerClockSnapshot
    {
        public ServerClockSnapshot(long tick, int tickRate, long epochUnixMilliseconds, double globalTimeSeconds)
        {
            Tick = tick;
            TickRate = tickRate;
            EpochUnixMilliseconds = epochUnixMilliseconds;
            GlobalTimeSeconds = globalTimeSeconds;
        }

        public long Tick { get; }
        public int TickRate { get; }
        public long EpochUnixMilliseconds { get; }
        public double GlobalTimeSeconds { get; }
    }
}
