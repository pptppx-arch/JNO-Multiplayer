namespace Assets.Scripts.Multiplayer.Telemetry
{
    using System;

    /// <summary>
    /// Defines the accepted physical envelope for multiplayer telemetry.
    /// The host must call TryValidateAndNormalize before caching or relaying any
    /// client-supplied packet. Clients call it again only as a float-safety guard.
    /// </summary>
    public static class TelemetryValidator
    {
        // Starting values for crafts operating around one spawning planet. Tune these
        // deliberately for the largest planet and fastest vehicle your server supports.
        public const double MaximumPciRadiusMeters = 100000000.0;            // 100,000 km from PCI origin
        public const double MaximumLinearSpeedMetersPerSecond = 25000.0;     // 25 km/s
        public const double MaximumAngularSpeedRadiansPerSecond = 100.0;     // rad/s

        // A valid rotation should already be close to unit length. Small numerical drift
        // is normalized; malformed or deliberately extreme rotations are rejected.
        public const double MinimumQuaternionMagnitude = 0.001;
        public const double MaximumQuaternionMagnitudeDeviation = 0.10;

        public static bool TryValidateAndNormalize(ref TelemetryPacket packet, out string reason)
        {
            reason = null;

            if (!packet.IsFinite)
            {
                reason = "packet contains a non-finite numeric value";
                return false;
            }

            if (!HasMagnitudeAtMost(
                    packet.PositionX,
                    packet.PositionY,
                    packet.PositionZ,
                    MaximumPciRadiusMeters))
            {
                reason = "PCI position exceeds the configured operating radius";
                return false;
            }

            if (!HasMagnitudeAtMost(
                    packet.VelocityX,
                    packet.VelocityY,
                    packet.VelocityZ,
                    MaximumLinearSpeedMetersPerSecond))
            {
                reason = "linear velocity exceeds the configured limit";
                return false;
            }

            if (!HasMagnitudeAtMost(
                    packet.AngularVelocityX,
                    packet.AngularVelocityY,
                    packet.AngularVelocityZ,
                    MaximumAngularSpeedRadiansPerSecond))
            {
                reason = "angular velocity exceeds the configured limit";
                return false;
            }

            double quaternionMagnitudeSquared =
                packet.RotationX * packet.RotationX
                + packet.RotationY * packet.RotationY
                + packet.RotationZ * packet.RotationZ
                + packet.RotationW * packet.RotationW;

            if (double.IsNaN(quaternionMagnitudeSquared)
                || double.IsInfinity(quaternionMagnitudeSquared)
                || quaternionMagnitudeSquared < MinimumQuaternionMagnitude * MinimumQuaternionMagnitude)
            {
                reason = "rotation quaternion has near-zero or invalid magnitude";
                return false;
            }

            double quaternionMagnitude = Math.Sqrt(quaternionMagnitudeSquared);
            if (Math.Abs(quaternionMagnitude - 1.0) > MaximumQuaternionMagnitudeDeviation)
            {
                reason = "rotation quaternion is outside the allowed normalization tolerance";
                return false;
            }

            double inverseMagnitude = 1.0 / quaternionMagnitude;
            packet.RotationX *= inverseMagnitude;
            packet.RotationY *= inverseMagnitude;
            packet.RotationZ *= inverseMagnitude;
            packet.RotationW *= inverseMagnitude;
            return true;
        }

        private static bool HasMagnitudeAtMost(double x, double y, double z, double maximumMagnitude)
        {
            // Check components first. This avoids overflow during squaring and gives a
            // clear rejection for absurd-but-finite values before Unity float conversion.
            if (Math.Abs(x) > maximumMagnitude
                || Math.Abs(y) > maximumMagnitude
                || Math.Abs(z) > maximumMagnitude)
            {
                return false;
            }

            double magnitudeSquared = x * x + y * y + z * z;
            return magnitudeSquared <= maximumMagnitude * maximumMagnitude;
        }
    }
}
