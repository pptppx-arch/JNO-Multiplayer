namespace Assets.Scripts.Multiplayer.Telemetry
{
    using System;
    using System.Globalization;
    using System.Text;

    /// <summary>
    /// Shared UDP craft-state payload. Every physical value is a double; no Unity float
    /// type appears in the wire contract.
    /// </summary>
    public struct TelemetryPacket
    {
        public const string PacketType = "TEL1";
        public const int FieldCount = 18;
        public const int SessionTokenLength = 43;
        public string SessionToken;

        public int ClientId;
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

        public bool IsFinite
        {
            get
            {
                return IsFiniteDouble(PositionX) && IsFiniteDouble(PositionY) && IsFiniteDouble(PositionZ)
                    && IsFiniteDouble(VelocityX) && IsFiniteDouble(VelocityY) && IsFiniteDouble(VelocityZ)
                    && IsFiniteDouble(RotationX) && IsFiniteDouble(RotationY)
                    && IsFiniteDouble(RotationZ) && IsFiniteDouble(RotationW)
                    && IsFiniteDouble(AngularVelocityX) && IsFiniteDouble(AngularVelocityY)
                    && IsFiniteDouble(AngularVelocityZ);
            }
        }

        public string Serialize()
        {
            if (!IsValidSessionToken(SessionToken)) throw new InvalidOperationException("Telemetry packet has no valid UDP session token.");
            var builder = new StringBuilder(384);
            builder.Append(PacketType).Append('|');
            builder.Append(ClientId).Append('|');
            builder.Append(HostTick).Append('|');
            builder.Append(Sequence).Append('|');
            builder.Append(SessionToken).Append('|');

            AppendDouble(builder, PositionX);
            AppendDouble(builder, PositionY);
            AppendDouble(builder, PositionZ);
            AppendDouble(builder, VelocityX);
            AppendDouble(builder, VelocityY);
            AppendDouble(builder, VelocityZ);
            AppendDouble(builder, RotationX);
            AppendDouble(builder, RotationY);
            AppendDouble(builder, RotationZ);
            AppendDouble(builder, RotationW);
            AppendDouble(builder, AngularVelocityX);
            AppendDouble(builder, AngularVelocityY);
            AppendDouble(builder, AngularVelocityZ, appendSeparator: false);
            return builder.ToString();
        }

        public static bool TryParse(string payload, out TelemetryPacket packet)
        {
            packet = default(TelemetryPacket);
            if (string.IsNullOrWhiteSpace(payload)) return false;

            string[] fields = payload.Split('|');
            if (fields.Length != FieldCount
                || !string.Equals(fields[0], PacketType, StringComparison.Ordinal))
            {
                return false;
            }

            packet.SessionToken = fields[4];

            if (!int.TryParse(fields[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out packet.ClientId)
                || !long.TryParse(fields[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out packet.HostTick)
                || !uint.TryParse(fields[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out packet.Sequence)
                || !IsValidSessionToken(packet.SessionToken)
                || !TryReadDouble(fields[5], out packet.PositionX)
                || !TryReadDouble(fields[6], out packet.PositionY)
                || !TryReadDouble(fields[7], out packet.PositionZ)
                || !TryReadDouble(fields[8], out packet.VelocityX)
                || !TryReadDouble(fields[9], out packet.VelocityY)
                || !TryReadDouble(fields[10], out packet.VelocityZ)
                || !TryReadDouble(fields[11], out packet.RotationX)
                || !TryReadDouble(fields[12], out packet.RotationY)
                || !TryReadDouble(fields[13], out packet.RotationZ)
                || !TryReadDouble(fields[14], out packet.RotationW)
                || !TryReadDouble(fields[15], out packet.AngularVelocityX)
                || !TryReadDouble(fields[16], out packet.AngularVelocityY)
                || !TryReadDouble(fields[17], out packet.AngularVelocityZ))
            {
                return false;
            }

            return packet.ClientId >= 0 && packet.HostTick >= 0 && packet.IsFinite;
        }

        private static bool TryReadDouble(string text, out double value)
        {
            return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
                && IsFiniteDouble(value);
        }

        private static void AppendDouble(StringBuilder builder, double value, bool appendSeparator = true)
        {
            builder.Append(value.ToString("R", CultureInfo.InvariantCulture));
            if (appendSeparator) builder.Append('|');
        }

        private static bool IsFiniteDouble(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }
        public static bool IsValidSessionToken(string token)
        {
            if (string.IsNullOrEmpty(token) || token.Length != SessionTokenLength)
            {
                return false;
            }

            for (int i = 0; i < token.Length; i++)
            {
                char c = token[i];
                bool upper = c >= 'A' && c <= 'Z';
                bool lower = c >= 'a' && c <= 'z';
                bool digit = c >= '0' && c <= '9';
                if (!upper && !lower && !digit && c != '-' && c != '_')
                {
                    return false;
                }
            }

            return true;
        }

        public static bool TokensEqual(string expected, string actual)
        {
            if (!IsValidSessionToken(expected) || !IsValidSessionToken(actual))
            {
                return false;
            }

            int difference = 0;
            for (int i = 0; i < SessionTokenLength; i++)
            {
                difference |= expected[i] ^ actual[i];
            }

            return difference == 0;
        }
    }
}
