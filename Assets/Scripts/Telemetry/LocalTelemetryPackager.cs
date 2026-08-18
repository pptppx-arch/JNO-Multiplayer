namespace Assets.Scripts.Multiplayer.Telemetry
{
    using System;
    using System.Globalization;
    using System.Net;
    using System.Text;
    using System.Threading.Tasks;
    using Assets.Scripts.Flight.Sim;
    using ModApi.Craft;

    // Send-only local craft telemetry packager.
    public sealed class LocalCraftTelemetrySender
    {
        private const string PacketType = "TEL1";

        private readonly UdpNetworkHandler _udpNetworkHandler;
        private readonly IPEndPoint _hostEndPoint;
        private readonly int _localCraftId;
        private readonly double _sendIntervalSeconds;
        private readonly Func<CraftNode> _localCraftProvider;

        private double _nextSendTimeSeconds;
        private uint _nextSequence;

        public LocalCraftTelemetrySender(
            UdpNetworkHandler udpNetworkHandler,
            IPEndPoint hostEndPoint,
            int localCraftId,
            Func<CraftNode> localCraftProvider,
            double sendRateHz = 20.0)
        {
            if (udpNetworkHandler == null) throw new ArgumentNullException(nameof(udpNetworkHandler));
            if (hostEndPoint == null) throw new ArgumentNullException(nameof(hostEndPoint));
            if (localCraftProvider == null) throw new ArgumentNullException(nameof(localCraftProvider));
            if (localCraftId < 0) throw new ArgumentOutOfRangeException(nameof(localCraftId));
            if (sendRateHz <= 0.0) throw new ArgumentOutOfRangeException(nameof(sendRateHz));

            _udpNetworkHandler = udpNetworkHandler;
            _hostEndPoint = hostEndPoint;
            _localCraftId = localCraftId;
            _localCraftProvider = localCraftProvider;
            _sendIntervalSeconds = 1.0 / sendRateHz;
        }

        /// <summary>
        /// Call from the Juno game thread. Sends only when the configured interval is due.
        /// </summary>
        public Task SendIfDueAsync(double currentTimeSeconds, long lastKnownHostTick)
        {
            if (currentTimeSeconds < _nextSendTimeSeconds)
            {
                return Task.FromResult(0);
            }

            _nextSendTimeSeconds = currentTimeSeconds + _sendIntervalSeconds;
            return SendNowAsync(lastKnownHostTick);
        }

        /// <summary>
        /// Immediately samples and sends the local craft. Call once after TCP handshake
        /// to let the host discover this client's UDP endpoint.
        /// </summary>
        public Task SendNowAsync(long lastKnownHostTick)
        {
            CraftNode localCraft = _localCraftProvider();
            LocalCraftTelemetrySample sample;

            if (localCraft == null || !TryPackage(localCraft, lastKnownHostTick, out sample))
            {
                return Task.FromResult(0);
            }

            return _udpNetworkHandler.SendAsync(Serialize(sample), _hostEndPoint);
        }

        private bool TryPackage(CraftNode craftNode, long lastKnownHostTick, out LocalCraftTelemetrySample sample)
        {
            sample = default(LocalCraftTelemetrySample);

            ICraftFlightData flightData = craftNode as ICraftFlightData;
            ICraftNode craftData = craftNode as ICraftNode;

            if (flightData == null || craftData == null)
            {
                Mod.LogWarning("[LocalTelemetry] Local CraftNode does not expose ICraftFlightData/ICraftNode.");
                return false;
            }

            var position = flightData.Position;
            var velocity = flightData.Velocity;
            var angularVelocity = flightData.AngularVelocity;
            var rotation = craftData.Heading;

            if (!IsFinite(position.x) || !IsFinite(position.y) || !IsFinite(position.z) ||
                !IsFinite(velocity.x) || !IsFinite(velocity.y) || !IsFinite(velocity.z) ||
                !IsFinite(angularVelocity.x) || !IsFinite(angularVelocity.y) || !IsFinite(angularVelocity.z) ||
                !IsFinite(rotation.x) || !IsFinite(rotation.y) || !IsFinite(rotation.z) || !IsFinite(rotation.w))
            {
                Mod.LogWarning("[LocalTelemetry] Ignored a non-finite local craft state.");
                return false;
            }

            sample.CraftId = _localCraftId;
            sample.HostTick = lastKnownHostTick;
            sample.Sequence = unchecked(++_nextSequence);

            sample.PositionX = position.x;
            sample.PositionY = position.y;
            sample.PositionZ = position.z;

            sample.VelocityX = velocity.x;
            sample.VelocityY = velocity.y;
            sample.VelocityZ = velocity.z;

            sample.RotationX = rotation.x;
            sample.RotationY = rotation.y;
            sample.RotationZ = rotation.z;
            sample.RotationW = rotation.w;

            sample.AngularVelocityX = angularVelocity.x;
            sample.AngularVelocityY = angularVelocity.y;
            sample.AngularVelocityZ = angularVelocity.z;

            return true;
        }

        /// <summary>
        /// Packet format:
        /// TEL1|craftId|hostTick|sequence|posX|posY|posZ|velX|velY|velZ|
        /// rotX|rotY|rotZ|rotW|angularVelX|angularVelY|angularVelZ
        ///
        /// All craft-state components are serialized as invariant-culture round-trip
        /// doubles. The future receiver should parse these fields as doubles too.
        /// </summary>
        private static string Serialize(LocalCraftTelemetrySample sample)
        {
            var builder = new StringBuilder(384);

            builder.Append(PacketType).Append('|');
            builder.Append(sample.CraftId).Append('|');
            builder.Append(sample.HostTick).Append('|');
            builder.Append(sample.Sequence).Append('|');

            AppendDouble(builder, sample.PositionX);
            AppendDouble(builder, sample.PositionY);
            AppendDouble(builder, sample.PositionZ);

            AppendDouble(builder, sample.VelocityX);
            AppendDouble(builder, sample.VelocityY);
            AppendDouble(builder, sample.VelocityZ);

            AppendDouble(builder, sample.RotationX);
            AppendDouble(builder, sample.RotationY);
            AppendDouble(builder, sample.RotationZ);
            AppendDouble(builder, sample.RotationW);

            AppendDouble(builder, sample.AngularVelocityX);
            AppendDouble(builder, sample.AngularVelocityY);
            AppendDouble(builder, sample.AngularVelocityZ, appendSeparator: false);

            return builder.ToString();
        }

        private static void AppendDouble(StringBuilder builder, double value, bool appendSeparator = true)
        {
            builder.Append(value.ToString("R", CultureInfo.InvariantCulture));
            if (appendSeparator)
            {
                builder.Append('|');
            }
        }

        private static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }
    }

    /// <summary>
    /// Packet-ready local craft state. Every physical field is a double so no precision
    /// is lost after sampling the Juno-provided state components.
    /// </summary>
    public struct LocalCraftTelemetrySample
    {
        public int CraftId;
        public long HostTick;
        public uint Sequence;

        public double PositionX;
        public double PositionY;
        public double PositionZ;

        public double VelocityX;
        public double VelocityY;
        public double VelocityZ;

        public double RotationX;
        public double RotationY;
        public double RotationZ;
        public double RotationW;

        public double AngularVelocityX;
        public double AngularVelocityY;
        public double AngularVelocityZ;
    }
}
