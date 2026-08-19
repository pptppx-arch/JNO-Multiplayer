namespace Assets.Scripts.Multiplayer.Telemetry
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Net;
    using System.Threading.Tasks;
    using Assets.Scripts.Flight.Sim;
    using Assets.Scripts.Multiplayer;
    using ModApi.Craft;
    using UnityEngine;

    /// <summary>
    /// Generic UDP receive half for a TelemetryPacket stream. Socket reads run on an
    /// asynchronous task; packet consumers run only when Pump is called on the game
    /// thread. This prevents UDP tasks from touching Juno/Unity craft objects directly.
    /// </summary>
    public sealed class TelemetryReceiver
    {
        private readonly UdpNetworkHandler _network;
        private readonly ConcurrentQueue<ReceivedTelemetry> _pending = new ConcurrentQueue<ReceivedTelemetry>();
        private readonly Dictionary<int, uint> _lastAppliedSequence = new Dictionary<int, uint>();

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
            _lastAppliedSequence.Clear();
        }

        /// <summary>
        /// Dequeues all valid packets for a caller that needs raw packet handling, such
        /// as HostTelemetryUpdater. This must be called on the game thread.
        /// </summary>
        public int Pump(Action<TelemetryPacket, IPEndPoint> packetHandler)
        {
            if (packetHandler == null) throw new ArgumentNullException(nameof(packetHandler));

            int processed = 0;
            while (_pending.TryDequeue(out ReceivedTelemetry received))
            {
                packetHandler(received.Packet, received.RemoteEndPoint);
                processed++;
            }
            return processed;
        }

        /// <summary>
        /// Client-side packet consumer. Looks up the matching remote proxy through
        /// CraftRegistry and applies a smoothed kinematic Rigidbody pose. Packets for
        /// the local client or stale/out-of-order sequences are ignored.
        /// </summary>
        public int PumpRemoteProxies(int localClientId, float deltaTime, float positionLerpRate = 12f, float rotationSlerpRate = 12f)
        {
            int applied = 0;
            while (_pending.TryDequeue(out ReceivedTelemetry received))
            {
                TelemetryPacket packet = received.Packet;
                if (packet.ClientId == localClientId || !IsNewSequence(packet.ClientId, packet.Sequence))
                {
                    continue;
                }

                LastObservedHostTick = System.Math.Max(LastObservedHostTick, packet.HostTick);

                CraftNode remoteCraft = CraftRegistry.GetCraft(packet.ClientId);
                if (remoteCraft == null)
                {
                    // XML spawn may arrive after UDP. Dropping this packet is safe because
                    // the host continuously relays later fresh state for this client ID.
                    continue;
                }

                if (ApplyToRemoteProxy(remoteCraft, packet, deltaTime, positionLerpRate, rotationSlerpRate))
                {
                    _lastAppliedSequence[packet.ClientId] = packet.Sequence;
                    applied++;
                }
            }
            return applied;
        }

        /// <summary>
        /// Applies a packet to a spawned remote craft. ICraftDebris exposes the Unity
        /// Rigidbody used by the proxy. This assumes the packet coordinates are in the
        /// same reference frame represented by that Rigidbody; place any Juno-specific
        /// PCI-to-local conversion immediately before targetPosition if your build
        /// requires one.
        /// </summary>
        public static bool ApplyToRemoteProxy(CraftNode remoteCraft, TelemetryPacket packet, float deltaTime, float positionLerpRate = 12f, float rotationSlerpRate = 12f)
        {
            ICraftDebris craftDebris = remoteCraft as ICraftDebris;
            Rigidbody body = craftDebris == null ? null : craftDebris.RigidBody;
            if (body == null)
            {
                Mod.LogWarning("[TelemetryReceiver] Remote craft has no accessible Rigidbody.");
                return false;
            }

            body.isKinematic = true;

            Vector3 targetPosition = new Vector3(
                (float)packet.PositionX,
                (float)packet.PositionY,
                (float)packet.PositionZ);
            Quaternion targetRotation = new Quaternion(
                (float)packet.RotationX,
                (float)packet.RotationY,
                (float)packet.RotationZ,
                (float)packet.RotationW);

            float positionT = Mathf.Clamp01(positionLerpRate * System.Math.Max(0f, deltaTime));
            float rotationT = Mathf.Clamp01(rotationSlerpRate * System.Math.Max(0f, deltaTime));
            body.position = Vector3.Lerp(body.position, targetPosition, positionT);
            body.rotation = Quaternion.Slerp(body.rotation, targetRotation, rotationT);

            // Rigidbody velocity fields retain the newest received motion information
            // for later extrapolation/collision handling, while isKinematic prevents the
            // remote proxy from participating in local dynamic simulation.
            body.velocity = new Vector3((float)packet.VelocityX, (float)packet.VelocityY, (float)packet.VelocityZ);
            body.angularVelocity = new Vector3(
                (float)packet.AngularVelocityX,
                (float)packet.AngularVelocityY,
                (float)packet.AngularVelocityZ);
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

                    if (TelemetryPacket.TryParse(payload, out TelemetryPacket packet))
                    {
                        _pending.Enqueue(new ReceivedTelemetry(packet, remoteEndPoint));
                    }
                    else
                    {
                        Mod.LogWarning("[TelemetryReceiver] Ignored malformed UDP telemetry packet.");
                    }
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
    }
}
