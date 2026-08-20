namespace Assets.Scripts.Multiplayer.Telemetry
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Net;
    using Assets.Scripts.Flight.Sim;
    using Assets.Scripts.Multiplayer;

    /// <summary>
    /// One host-wide UDP telemetry service for the whole session. It receives all
    /// clients on one UDP port, binds each TCP-assigned client ID to its first accepted
    /// UDP endpoint, and relays each latest state to all other registered endpoints.
    /// </summary>
    public sealed class HostTelemetryUpdater : IDisposable
    {
        private readonly UdpNetworkHandler _udp;
        private readonly TelemetryReceiver _receiver;
        private readonly LocalTelemetryPackager _hostPackager;
        private readonly Dictionary<int, IPEndPoint> _udpEndPoints = new Dictionary<int, IPEndPoint>();
        private readonly Dictionary<int, TelemetryPacket> _latestState = new Dictionary<int, TelemetryPacket>();
        private readonly Dictionary<int, uint> _lastClientSequence = new Dictionary<int, uint>();
        private readonly ConcurrentQueue<int> _pendingClientRemovals = new ConcurrentQueue<int>();
        private readonly int _relayEveryTicks;

        private long _currentHostTick;
        private bool _started;

        public HostTelemetryUpdater(int udpPort, int hostClientId, int hostTickRate = 60, double relayRateHz = 20.0)
        {
            if (udpPort <= 0 || udpPort > 65535) throw new ArgumentOutOfRangeException(nameof(udpPort));
            if (hostTickRate <= 0) throw new ArgumentOutOfRangeException(nameof(hostTickRate));
            if (relayRateHz <= 0.0) throw new ArgumentOutOfRangeException(nameof(relayRateHz));

            _udp = new UdpNetworkHandler(udpPort);
            _receiver = new TelemetryReceiver(_udp);
            _hostPackager = new LocalTelemetryPackager(hostClientId);
            _relayEveryTicks = System.Math.Max(1, (int)System.Math.Round(hostTickRate / relayRateHz));
        }

        public void Start()
        {
            if (_started) return;
            _started = true;
            _receiver.Start();
            Mod.Log("[HostTelemetryUpdater] Shared host UDP relay started.");
        }

        /// <summary>
        /// Call once per authoritative host simulation tick, on the game thread.
        /// </summary>
        public void Pump(long hostTick)
        {
            if (!_started) return;
            _currentHostTick = hostTick;
            DrainPendingClientRemovals();

            _receiver.Pump(AcceptClientPacket);

            if (hostTick % _relayEveryTicks != 0) return;
            PackageHostState(hostTick);
            RelayLatestStates();
        }

        /// <summary>
        /// Safe to call from the TCP connection task. Actual dictionary mutation occurs
        /// at the next game-thread Pump call.
        /// </summary>
        public void RemoveClient(int clientId)
        {
            _pendingClientRemovals.Enqueue(clientId);
        }

        public void Dispose()
        {
            _started = false;
            _receiver.Stop();
            _udp.Close();
            _udpEndPoints.Clear();
            _latestState.Clear();
            _lastClientSequence.Clear();
            while (_pendingClientRemovals.TryDequeue(out _)) { }
        }

        private void AcceptClientPacket(TelemetryPacket packet, IPEndPoint remoteEndPoint)
        {
            // Packet client ID must map to an active TCP session and the source IP must
            // match that session's TCP peer. This prevents a random internet sender from
            // claiming another assigned ID. The UDP source port is learned once.
            if (!ServerConnection.IsRemoteSessionActive(packet.ClientId)
                || !ServerConnection.IsExpectedClientAddress(packet.ClientId, remoteEndPoint.Address))
            {
                Mod.LogWarning($"[HostTelemetryUpdater] Rejected telemetry from unknown/mismatched Client ID {packet.ClientId}.");
                return;
            }

            if (_udpEndPoints.TryGetValue(packet.ClientId, out IPEndPoint existingEndpoint))
            {
                if (!EndpointsEqual(existingEndpoint, remoteEndPoint))
                {
                    Mod.LogWarning($"[HostTelemetryUpdater] Rejected changed UDP endpoint for Client ID {packet.ClientId}.");
                    return;
                }
            }
            else
            {
                _udpEndPoints[packet.ClientId] = remoteEndPoint;
                Mod.Log($"[HostTelemetryUpdater] Registered UDP endpoint for Client ID {packet.ClientId}: {remoteEndPoint}.");
            }

            if (!ServerConnection.IsRemoteSessionActive(packet.ClientId) || !ServerConnection.IsExpectedClientAddress(packet.ClientId, remoteEndPoint.Address) || !ServerConnection.IsExpectedUdpToken(packet.ClientId, packet.SessionToken))
            {
                Mod.LogWarning($"[HostTelemetryUpdater] Rejected telemetry with invalid session, source IP, or UDP token for Client ID {packet.ClientId}.");
                return;
            }

            if (_lastClientSequence.TryGetValue(packet.ClientId, out uint lastSequence) && !IsNewerSequence(packet.Sequence, lastSequence))
            {
                return;
            }

            // The host is authoritative for the packet's simulation tick; a client can
            // report its last seen host tick, but it cannot choose the relayed tick.
            packet.HostTick = _currentHostTick;
            _lastClientSequence[packet.ClientId] = packet.Sequence;
            _latestState[packet.ClientId] = packet;

            // The host also has a locally spawned kinematic proxy for each remote
            // client. Update that proxy on the host game thread before relaying state.
            CraftNode hostSideProxy = CraftRegistry.GetCraft(packet.ClientId);
            if (hostSideProxy != null)
            {
                TelemetryReceiver.ApplyToRemoteProxy(hostSideProxy, packet, 1f / 60f);
            }
        }

        private void DrainPendingClientRemovals()
        {
            while (_pendingClientRemovals.TryDequeue(out int clientId))
            {
                _udpEndPoints.Remove(clientId);
                _latestState.Remove(clientId);
                _lastClientSequence.Remove(clientId);
            }
        }

        private void PackageHostState(long hostTick)
        {
            CraftNode hostCraft = CraftRegistry.GetCraft(ServerConnection.HostClientId);
            if (hostCraft != null && _hostPackager.TryPackage(hostCraft, hostTick, out TelemetryPacket hostPacket))
            {
                _latestState[hostPacket.ClientId] = hostPacket;
            }
        }

        private void RelayLatestStates()
        {
            foreach (KeyValuePair<int, IPEndPoint> destination in _udpEndPoints)
            {
                int destinationClientId = destination.Key;
                IPEndPoint destinationEndPoint = destination.Value;

                foreach (KeyValuePair<int, TelemetryPacket> state in _latestState)
                {
                    TelemetryPacket relayPacket = state.Value;
                    relayPacket.SessionToken = GetUdpSessionToken(destinationClientId);

                    if (!string.IsNullOrEmpty(relayPacket.SessionToken))
                    {
                        _ = _udp.SendAsync(relayPacket.Serialize(), destinationEndPoint);
                    }
                }
            }
        }
        private static string GetUdpSessionToken(int clientId)
        {
            lock (ServerConnection.Sessions)
            {
                ClientSession session = ServerConnection.Sessions.Find(s => s.Id == clientId);
                return session == null ? string.Empty : session.UdpSessionToken;
            }
        }
        private static bool EndpointsEqual(IPEndPoint left, IPEndPoint right)
        {
            return left != null && right != null && left.Port == right.Port && left.Address.Equals(right.Address);
        }

        private static bool IsNewerSequence(uint candidate, uint previous)
        {
            return candidate != previous && unchecked(candidate - previous) < 0x80000000u;
        }
    }
}
