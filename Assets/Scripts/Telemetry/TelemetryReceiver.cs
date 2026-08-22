namespace Assets.Scripts.Multiplayer.Telemetry
{
    using Assets.Scripts.Flight.Sim;
    using Assets.Scripts.Multiplayer;
    using Assets.Scripts.Clock;
    using ModApi.Craft;
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Net;
    using System.Threading;
    using System.Threading.Tasks;
    using UnityEngine;

    /// <summary>
    /// Generic UDP receive half for a telemetry stream. Socket reads are asynchronous;
    /// packet consumption and all Juno/Unity operations occur on the game thread.
    /// </summary>
    public sealed class TelemetryReceiver
    {
        private const int MaximumPayloadCharacters = 1024;
        private const int MaximumPendingPackets = 256;
        private const int MaximumPacketsPerPump = 64;
        private const int SnapshotHistoryCapacity = 24;
        private const int InterpolationDelayTicks = 6;
        private const int MaximumExtrapolationTicks = 6;
        private const double DropLogIntervalSeconds = 5.0;

        private readonly UdpNetworkHandler _network;
        private readonly ConcurrentQueue<ReceivedTelemetry> _pending =
            new ConcurrentQueue<ReceivedTelemetry>();
        private readonly Dictionary<int, uint> _lastAppliedSequence =
            new Dictionary<int, uint>();
        private readonly Dictionary<int, List<TelemetryPacket>> _snapshotHistory =
            new Dictionary<int, List<TelemetryPacket>>();

        private int _pendingCount;
        private int _droppedSinceLastLog;
        private long _nextDropLogTimestamp;
        private bool _running;

        public long LastObservedHostTick { get; private set; }

        public TelemetryReceiver(UdpNetworkHandler network)
        {
            _network = network ?? throw new ArgumentNullException(nameof(network));
        }

        public void Start()
        {
            if (_running) return;
            _running = true;
            _ = ReceiveLoopAsync();
        }

        public void Stop()
        {
            _running = false;
            while (_pending.TryDequeue(out _)) { }
            Interlocked.Exchange(ref _pendingCount, 0);
            _lastAppliedSequence.Clear();
            _snapshotHistory.Clear();
            LastObservedHostTick = 0;
        }

        /// <summary>
        /// Dequeues a bounded number of parsed packets for host-side handling. This method
        /// must be invoked on the game thread.
        /// </summary>
        public int Pump(Action<TelemetryPacket, IPEndPoint> packetHandler)
        {
            if (packetHandler == null) throw new ArgumentNullException(nameof(packetHandler));

            int processed = 0;
            while (processed < MaximumPacketsPerPump
                && _pending.TryDequeue(out ReceivedTelemetry received))
            {
                Interlocked.Decrement(ref _pendingCount);
                packetHandler(received.Packet, received.RemoteEndPoint);
                processed++;
            }
            return processed;
        }

        /// <summary>
        /// Client-side packet consumer. It verifies the authenticated host endpoint and
        /// token, rejects stale packets, and applies a bounded amount of remote state.
        /// </summary>
        public int PumpRemoteProxies(
            int localClientId,
            float deltaTime,
            IPEndPoint expectedHostEndPoint,
            string expectedSessionToken,
            ClientClockSynchronizer clockSynchronizer,
            float positionLerpRate = 12f,
            float rotationSlerpRate = 12f)
        {
            if (expectedHostEndPoint == null) throw new ArgumentNullException(nameof(expectedHostEndPoint));
            if (!TelemetryPacket.IsValidSessionToken(expectedSessionToken))
            {
                throw new ArgumentException("A valid UDP session token is required.", nameof(expectedSessionToken));
            }

            int applied = 0;
            int processed = 0;
            while (processed < MaximumPacketsPerPump
                && _pending.TryDequeue(out ReceivedTelemetry received))
            {
                Interlocked.Decrement(ref _pendingCount);
                processed++;
                TelemetryPacket packet = received.Packet;

                if (!EndpointsEqual(received.RemoteEndPoint, expectedHostEndPoint)
                    || !TelemetryPacket.TokensEqual(expectedSessionToken, packet.SessionToken)
                    || packet.ClientId == localClientId
                    || !IsNewSequence(packet.ClientId, packet.Sequence)
                    || !TelemetryValidator.TryValidateAndNormalize(ref packet, out _))
                {
                    continue;
                }

                LastObservedHostTick = Math.Max(LastObservedHostTick, packet.HostTick);
                clockSynchronizer?.ObserveTelemetryTick(packet.HostTick);
                _lastAppliedSequence[packet.ClientId] = packet.Sequence;
                AddSnapshot(packet);
            }

            int tickRate = clockSynchronizer == null ? 60 : clockSynchronizer.TickRate;
            long presentationTick = clockSynchronizer == null
                ? Math.Max(0, LastObservedHostTick - InterpolationDelayTicks)
                : clockSynchronizer.GetPresentationTick(InterpolationDelayTicks);

            var clientIds = new List<int>(_snapshotHistory.Keys);
            foreach (int clientId in clientIds)
            {
                if (clientId == localClientId
                    || !TryGetPresentationSnapshot(
                        clientId,
                        presentationTick,
                        tickRate,
                        out TelemetryPacket packet))
                {
                    continue;
                }

                CraftNode remoteCraft = CraftRegistry.GetCraft(clientId);
                if (remoteCraft == null)
                {
                    // Retain recent history. XML may arrive after its first UDP snapshots.
                    continue;
                }

                if (ApplyToRemoteProxy(remoteCraft, packet, deltaTime, positionLerpRate, rotationSlerpRate))
                {
                    applied++;
                }
            }
            return applied;
        }

        /// <summary>
        /// Applies validated PCI telemetry to a remote proxy. The craft's public Juno
        /// reference frame converts all position, velocity, rotation, and angular-vector
        /// fields before the final Unity float assignments.
        /// </summary>
        public static bool ApplyToRemoteProxy(
            CraftNode remoteCraft,
            TelemetryPacket packet,
            float deltaTime,
            float positionLerpRate = 12f,
            float rotationSlerpRate = 12f)
        {
            if (remoteCraft == null
                || !TelemetryValidator.TryValidateAndNormalize(ref packet, out _))
            {
                return false;
            }

            ICraftDebris craftDebris = remoteCraft as ICraftDebris;
            Rigidbody body = craftDebris == null ? null : craftDebris.RigidBody;
            var referenceFrame = remoteCraft.ReferenceFrame;
            if (body == null || referenceFrame == null)
            {
                Mod.LogWarning("[TelemetryReceiver] Remote craft has no accessible Rigidbody or reference frame.");
                return false;
            }

            var pciPosition = new Vector3d(packet.PositionX, packet.PositionY, packet.PositionZ);
            var pciVelocity = new Vector3d(packet.VelocityX, packet.VelocityY, packet.VelocityZ);
            var pciAngularVelocity = new Vector3d(
                packet.AngularVelocityX,
                packet.AngularVelocityY,
                packet.AngularVelocityZ);
            var pciRotation = new Quaterniond(
                packet.RotationX,
                packet.RotationY,
                packet.RotationZ,
                packet.RotationW);

            var framePosition = referenceFrame.PlanetToFramePositiond(pciPosition);
            var frameVelocity = referenceFrame.PlanetToFrameVelocity(pciVelocity);
            var frameAngularVelocity = referenceFrame.PlanetToFrameVector(pciAngularVelocity);
            var frameRotation = referenceFrame.PlanetToFrameRotation(pciRotation);

            body.isKinematic = true;
            Vector3 targetPosition = new Vector3(
                (float)framePosition.x,
                (float)framePosition.y,
                (float)framePosition.z);
            Quaternion targetRotation = new Quaternion(
                frameRotation.x,
                frameRotation.y,
                frameRotation.z,
                frameRotation.w);

            float positionT = Mathf.Clamp01(positionLerpRate * Math.Max(0f, deltaTime));
            float rotationT = Mathf.Clamp01(rotationSlerpRate * Math.Max(0f, deltaTime));
            body.position = Vector3.Lerp(body.position, targetPosition, positionT);
            body.rotation = Quaternion.Slerp(body.rotation, targetRotation, rotationT);
            body.velocity = frameVelocity;
            body.angularVelocity = frameAngularVelocity;
            return true;
        }

        private async Task ReceiveLoopAsync()
        {
            while (_running)
            {
                try
                {
                    var (payload, remoteEndPoint) = await _network.ReceiveAsync();
                    if (!_running) break;
                    if (payload == null || remoteEndPoint == null) continue;

                    if (payload.Length > MaximumPayloadCharacters)
                    {
                        RecordDroppedPacket("oversized UDP telemetry");
                        continue;
                    }

                    if (!TelemetryPacket.TryParse(payload, out TelemetryPacket packet))
                    {
                        RecordDroppedPacket("malformed UDP telemetry");
                        continue;
                    }

                    if (Interlocked.Increment(ref _pendingCount) > MaximumPendingPackets)
                    {
                        Interlocked.Decrement(ref _pendingCount);
                        RecordDroppedPacket("UDP queue overflow");
                        continue;
                    }

                    _pending.Enqueue(new ReceivedTelemetry(packet, remoteEndPoint));
                }
                catch (Exception ex)
                {
                    if (_running)
                    {
                        Mod.LogWarning($"[TelemetryReceiver] UDP receive loop stopped: {ex.Message}");
                    }
                    break;
                }
            }
        }

        private void AddSnapshot(TelemetryPacket packet)
        {
            if (!_snapshotHistory.TryGetValue(packet.ClientId, out List<TelemetryPacket> snapshots))
            {
                snapshots = new List<TelemetryPacket>();
                _snapshotHistory.Add(packet.ClientId, snapshots);
            }

            for (int i = 0; i < snapshots.Count; i++)
            {
                if (snapshots[i].HostTick == packet.HostTick)
                {
                    snapshots[i] = packet;
                    return;
                }

                if (snapshots[i].HostTick > packet.HostTick)
                {
                    snapshots.Insert(i, packet);
                    TrimSnapshots(snapshots);
                    return;
                }
            }

            snapshots.Add(packet);
            TrimSnapshots(snapshots);
        }

        private bool TryGetPresentationSnapshot(
            int clientId,
            long presentationTick,
            int tickRate,
            out TelemetryPacket packet)
        {
            packet = default(TelemetryPacket);
            if (!_snapshotHistory.TryGetValue(clientId, out List<TelemetryPacket> snapshots)
                || snapshots.Count == 0
                || tickRate <= 0)
            {
                return false;
            }

            TelemetryPacket oldest = snapshots[0];
            TelemetryPacket newest = snapshots[snapshots.Count - 1];
            if (presentationTick < oldest.HostTick)
            {
                return false;
            }

            if (presentationTick >= newest.HostTick)
            {
                long extrapolationTicks = presentationTick - newest.HostTick;
                if (extrapolationTicks > MaximumExtrapolationTicks)
                {
                    return false;
                }

                packet = newest;
                double seconds = extrapolationTicks / (double)tickRate;
                packet.PositionX += packet.VelocityX * seconds;
                packet.PositionY += packet.VelocityY * seconds;
                packet.PositionZ += packet.VelocityZ * seconds;
                packet.HostTick = presentationTick;
                return TelemetryValidator.TryValidateAndNormalize(ref packet, out _);
            }

            for (int i = 1; i < snapshots.Count; i++)
            {
                TelemetryPacket newer = snapshots[i];
                if (newer.HostTick < presentationTick) continue;

                TelemetryPacket older = snapshots[i - 1];
                long tickSpan = newer.HostTick - older.HostTick;
                if (tickSpan <= 0)
                {
                    packet = newer;
                    return true;
                }

                double t = (presentationTick - older.HostTick) / (double)tickSpan;
                packet = Interpolate(older, newer, t);
                packet.HostTick = presentationTick;
                return TelemetryValidator.TryValidateAndNormalize(ref packet, out _);
            }

            return false;
        }

        private static void TrimSnapshots(List<TelemetryPacket> snapshots)
        {
            while (snapshots.Count > SnapshotHistoryCapacity)
            {
                snapshots.RemoveAt(0);
            }
        }

        private static TelemetryPacket Interpolate(
            TelemetryPacket older,
            TelemetryPacket newer,
            double t)
        {
            TelemetryPacket packet = newer;
            packet.PositionX = Lerp(older.PositionX, newer.PositionX, t);
            packet.PositionY = Lerp(older.PositionY, newer.PositionY, t);
            packet.PositionZ = Lerp(older.PositionZ, newer.PositionZ, t);
            packet.VelocityX = Lerp(older.VelocityX, newer.VelocityX, t);
            packet.VelocityY = Lerp(older.VelocityY, newer.VelocityY, t);
            packet.VelocityZ = Lerp(older.VelocityZ, newer.VelocityZ, t);
            packet.AngularVelocityX = Lerp(older.AngularVelocityX, newer.AngularVelocityX, t);
            packet.AngularVelocityY = Lerp(older.AngularVelocityY, newer.AngularVelocityY, t);
            packet.AngularVelocityZ = Lerp(older.AngularVelocityZ, newer.AngularVelocityZ, t);

            double newerX = newer.RotationX;
            double newerY = newer.RotationY;
            double newerZ = newer.RotationZ;
            double newerW = newer.RotationW;
            double dot = older.RotationX * newerX
                + older.RotationY * newerY
                + older.RotationZ * newerZ
                + older.RotationW * newerW;
            if (dot < 0.0)
            {
                newerX = -newerX;
                newerY = -newerY;
                newerZ = -newerZ;
                newerW = -newerW;
            }

            packet.RotationX = Lerp(older.RotationX, newerX, t);
            packet.RotationY = Lerp(older.RotationY, newerY, t);
            packet.RotationZ = Lerp(older.RotationZ, newerZ, t);
            packet.RotationW = Lerp(older.RotationW, newerW, t);
            return packet;
        }

        private static double Lerp(double from, double to, double t)
        {
            return from + ((to - from) * t);
        }

        private void RecordDroppedPacket(string reason)
        {
            Interlocked.Increment(ref _droppedSinceLastLog);
            long now = Stopwatch.GetTimestamp();
            long nextLog = Interlocked.Read(ref _nextDropLogTimestamp);
            if (now < nextLog
                || Interlocked.CompareExchange(
                    ref _nextDropLogTimestamp,
                    now + (long)(DropLogIntervalSeconds * Stopwatch.Frequency),
                    nextLog) != nextLog)
            {
                return;
            }

            int dropped = Interlocked.Exchange(ref _droppedSinceLastLog, 0);
            Mod.LogWarning($"[TelemetryReceiver] Dropped {dropped} packet(s): {reason}.");
        }

        private bool IsNewSequence(int clientId, uint candidate)
        {
            if (!_lastAppliedSequence.TryGetValue(clientId, out uint previous)) return true;
            return candidate != previous && unchecked(candidate - previous) < 0x80000000u;
        }

        private struct ReceivedTelemetry
        {
            public readonly TelemetryPacket Packet;
            public readonly IPEndPoint RemoteEndPoint;

            public ReceivedTelemetry(TelemetryPacket packet, IPEndPoint remoteEndPoint)
            {
                Packet = packet;
                RemoteEndPoint = remoteEndPoint;
            }
        }

        private static bool EndpointsEqual(IPEndPoint actual, IPEndPoint expected)
        {
            return actual != null
                && expected != null
                && actual.Port == expected.Port
                && actual.Address.Equals(expected.Address);
        }
    }
}
