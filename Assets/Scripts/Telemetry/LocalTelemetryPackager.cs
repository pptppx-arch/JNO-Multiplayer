namespace Assets.Scripts.Multiplayer.Telemetry
{
    using System;
    using Assets.Scripts.Flight.Sim;
    using ModApi.Craft;

    /// <summary>
    /// Packages only the current local craft state. It does not own a UDP socket,
    /// receive telemetry, or modify remote crafts; ClientTelemetryUpdater does that.
    /// </summary>
    public sealed class LocalTelemetryPackager
    {
        private readonly int _localClientId;
        private uint _nextSequence;

        public LocalTelemetryPackager(int localClientId)
        {
            if (localClientId < 0) throw new ArgumentOutOfRangeException(nameof(localClientId));
            _localClientId = localClientId;
        }

        /// <summary>
        /// Samples the Juno CraftNode on the game thread and returns a complete TEL1
        /// packet. The concrete Juno vector and quaternion types are deliberately
        /// inferred rather than imported by a guessed namespace.
        /// </summary>
        public bool TryPackage(CraftNode craftNode, long hostTick, out TelemetryPacket packet)
        {
            packet = default(TelemetryPacket);
            if (craftNode == null) return false;

            ICraftFlightData flightData = craftNode as ICraftFlightData;
            ICraftNode craftData = craftNode as ICraftNode;
            if (flightData == null || craftData == null)
            {
                Mod.LogWarning("[LocalTelemetryPackager] CraftNode does not expose ICraftFlightData/ICraftNode.");
                return false;
            }

            var position = flightData.Position;
            var velocity = flightData.Velocity;
            var angularVelocity = flightData.AngularVelocity;
            var rotation = craftData.Heading;

            packet.ClientId = _localClientId;
            packet.HostTick = hostTick;
            packet.Sequence = unchecked(++_nextSequence);

            packet.PositionX = position.x;
            packet.PositionY = position.y;
            packet.PositionZ = position.z;
            packet.VelocityX = velocity.x;
            packet.VelocityY = velocity.y;
            packet.VelocityZ = velocity.z;
            packet.RotationX = rotation.x;
            packet.RotationY = rotation.y;
            packet.RotationZ = rotation.z;
            packet.RotationW = rotation.w;
            packet.AngularVelocityX = angularVelocity.x;
            packet.AngularVelocityY = angularVelocity.y;
            packet.AngularVelocityZ = angularVelocity.z;

            if (!packet.IsFinite)
            {
                Mod.LogWarning("[LocalTelemetryPackager] Ignored non-finite local craft state.");
                return false;
            }

            return true;
        }
    }
}
