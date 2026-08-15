namespace Assets.Scripts.Multiplayer.Telemetry
{
    using System;
    using System.Net;
    using System.Net.Sockets;
    using System.Text;
    using System.Threading.Tasks;
    using UnityEngine;

    public static class TelemetryClient
    {
        private static UdpClient _udpClient;
        private static IPEndPoint _hostEndPoint;
        private static bool _isRunning;
        private static int tickRateHz = 50;

        public static void StartTelemetry(string host, int port, int localClientId)
        {
            if (_isRunning) return;

            try
            {
                _udpClient = new UdpClient();
                _hostEndPoint = new IPEndPoint(host, hostPort);
                _isRunning = true;

                Mod.Log($"[TelemetryClient] UDP Telemetry Client started. Target: {host}:{hostPort}");

                // Task 1: Stream local control inputs to host
                _ = SendInputLoopAsync(localClientId, tickRateHz);

                // Task 2: Listen for world transform updates from host
                _ = ReceiveStateLoopAsync();
            }
            catch (Exception ex)
            {
                Mod.LogError($"[TelemetryClient] Failed to start UDP Client: {ex.Message}");
            }
        }

        public static void StopTelemetry()
        {
            _isRunning = false;
            _udpClient?.Close();
            _udpClient = null;
            Mod.Log("[TelemetryClient] UDP Telemetry Client stopped.");
        }

        #region Sending Inputs
        private static async Task SendInputLoopAsync(int localClientId, int tickRateHz)
        {
            int delayMs = 1000 / tickRateHz;

            while (_isRunning && _udpClient != null)
            {
                try
                {
                    SendLocalInputs(localClientId);
                }
                catch (Exception ex)
                {
                    Mod.LogError($"[TelemetryClient] Send input error: {ex.Message}");
                }

                await Task.Delay(delayMs);
            }
        }

        private static void SendLocalInputs(int localClientId)
        {
            var craftNode = CraftRegistry.GetCraft(localClientId);
            if (craftNode == null) return;

            // Fetch input values (replace with your game's input axis getters)
            float pitch = Input.GetAxis("Vertical");
            float roll = Input.GetAxis("Horizontal");
            float yaw = Input.GetAxis("Yaw");
            int actionGroupMask = 0; // Pack active AGs into bitmask if needed

            // Format: "ClientId|Pitch,Roll,Yaw|AGMask"
            string payload = $"{localClientId}|{pitch:F2},{roll:F2},{yaw:F2}|{actionGroupMask}";
            byte[] bytes = Encoding.UTF8.GetBytes(payload);

            _udpClient.SendAsync(bytes, bytes.Length, _hostEndPoint);
        }
        #endregion

        #region Receiving Craft States
        private static async Task ReceiveStateLoopAsync()
        {
            while (_isRunning && _udpClient != null)
            {
                try
                {
                    var result = await _udpClient.ReceiveAsync();
                    string payload = Encoding.UTF8.GetString(result.Buffer);

                    ProcessHostStateUpdate(payload);
                }
                catch (ObjectDisposedException) { break; }
                catch (Exception ex)
                {
                    if (_isRunning) Mod.LogError($"[TelemetryClient] Receive state error: {ex.Message}");
                }
            }
        }

        private static void ProcessHostStateUpdate(string payload)
        {
            // Format: "ClientId|PosX,PosY,PosZ|RotX,RotY,RotZ,RotW|VelX,VelY,VelZ"
            string[] parts = payload.Split('|');
            if (parts.Length < 4) return;

            if (!int.TryParse(parts[0], out int remoteClientId)) return;

            // Ignore state updates for player's local craft if client is doing client-side prediction
            if (remoteClientId == ClientConnection.LocalClientId) return;

            var craftNode = CraftRegistry.GetCraft(remoteClientId);
            if (craftNode == null) return;

            // Parse Position
            string[] posStr = parts[1].Split(',');
            if (posStr.Length == 3 &&
                double.TryParse(posStr[0], out double px) &&
                double.TryParse(posStr[1], out double py) &&
                double.TryParse(posStr[2], out double pz))
            {
                //craftNode.Position = new Vector3d(px, py, pz);
            }

            // Parse Rotation
            string[] rotStr = parts[2].Split(',');
            if (rotStr.Length == 4 &&
                double.TryParse(rotStr[0], out double rx) &&
                double.TryParse(rotStr[1], out double ry) &&
                double.TryParse(rotStr[2], out double rz) &&
                double.TryParse(rotStr[3], out double rw))
            {
                //craftNode.Heading = new Quaterniond(rx, ry, rz, rw);
            }

            // Parse Velocity
            string[] velStr = parts[3].Split(',');
            if (velStr.Length == 3 &&
                double.TryParse(velStr[0], out double vx) &&
                double.TryParse(velStr[1], out double vy) &&
                double.TryParse(velStr[2], out double vz))
            {
                //craftNode.Velocity = new Vector3d(vx, vy, vz);
                //craftNode.GameObject.LocalPosition
            }
        }
        #endregion
    }
}