namespace Assets.Scripts.Multiplayer.Telemetry
{
    using System;
    using System.Collections.Concurrent;
    using System.Net;
    using System.Net.Sockets;
    using System.Text;
    using System.Threading.Tasks;
    using UnityEngine;

    public static class TelemetryHost
    {
        public const int UdpPort = 25556;
        private static UdpClient _udpListener;
        private static bool _isRunning;

        // Tracks Client ID -> UDP Endpoint to route outgoing state updates
        private static readonly ConcurrentDictionary<int, IPEndPoint> _clientEndpoints = new ConcurrentDictionary<int, IPEndPoint>();

        public static void StartTelemetry(int tickRateHz = 20)
        {
            if (_isRunning) return;

            try
            {
                _udpListener = new UdpClient(UdpPort);
                _isRunning = true;
                Mod.Log($"[TelemetryHost] UDP Telemetry Host active on port {UdpPort}.");

                // Task 1: Listen for client input packets
                _ = ReceiveInputLoopAsync();

                // Task 2: Broadcast physical world states to all clients
                _ = BroadcastStateLoopAsync(tickRateHz);
            }
            catch (Exception ex)
            {
                Mod.LogError($"[TelemetryHost] Failed to start UDP Host: {ex.Message}");
            }
        }

        public static void StopTelemetry()
        {
            _isRunning = false;
            _udpListener?.Close();
            _udpListener = null;
            _clientEndpoints.Clear();
            Mod.Log("[TelemetryHost] UDP Telemetry Host stopped.");
        }

        #region Receiving Inputs from Clients
        private static async Task ReceiveInputLoopAsync()
        {
            while (_isRunning && _udpListener != null)
            {
                try
                {
                    var result = await _udpListener.ReceiveAsync();
                    string payload = Encoding.UTF8.GetString(result.Buffer);

                    ProcessClientInput(payload, result.RemoteEndPoint);
                }
                catch (ObjectDisposedException) { break; }
                catch (Exception ex)
                {
                    if (_isRunning) Mod.LogError($"[TelemetryHost] Receive error: {ex.Message}");
                }
            }
        }

        private static void ProcessClientInput(string payload, IPEndPoint clientEndPoint)
        {
            // Format: "ClientId|Pitch,Roll,Yaw|AGMask"
            string[] parts = payload.Split('|');
            if (parts.Length < 3) return;

            if (!int.TryParse(parts[0], out int clientId)) return;

            // Register/update client's UDP endpoint so we know where to reply
            _clientEndpoints[clientId] = clientEndPoint;

            // Apply inputs to target craft on host
            var craftNode = CraftRegistry.GetCraft(clientId);
            if (craftNode == null) return;

            string[] inputs = parts[1].Split(',');
            if (inputs.Length == 3 &&
                float.TryParse(inputs[0], out float pitch) &&
                float.TryParse(inputs[1], out float roll) &&
                float.TryParse(inputs[2], out float yaw))
            {
                // Set control inputs on Host's simulated craft instance
                // e.g. craftNode.Controls.Pitch = pitch;
            }

            if (int.TryParse(parts[2], out int agMask))
            {
                // Apply Action Groups mask if applicable
            }
        }
        #endregion

        #region Broadcasting Craft Transforms to Clients
        private static async Task BroadcastStateLoopAsync(int tickRateHz)
        {
            int delayMs = 1000 / tickRateHz;

            while (_isRunning && _udpListener != null)
            {
                try
                {
                    BroadcastAllCraftStates();
                }
                catch (Exception ex)
                {
                    Mod.LogError($"[TelemetryHost] Broadcast error: {ex.Message}");
                }

                await Task.Delay(delayMs);
            }
        }

        private static void BroadcastAllCraftStates()
        {
            // Loop through all known sessions/clients and send their current craft transforms
            foreach (var kvp in _clientEndpoints)
            {
                int targetClientId = kvp.Key;
                IPEndPoint targetEndPoint = kvp.Value;

                var craftNode = CraftRegistry.GetCraft(targetClientId);
                if (craftNode == null) continue;

                Vector3d pos = craftNode.Position;
                Quaterniond rot = craftNode.Heading;
                Vector3d vel = craftNode.Velocity;

                // Format: "ClientId|PosX,PosY,PosZ|RotX,RotY,RotZ,RotW|VelX,VelY,VelZ"
                string statePayload = $"{targetClientId}|" +
                                      $"{pos.x:F3},{pos.y:F3},{pos.z:F3}|" +
                                      $"{rot.x:F4},{rot.y:F4},{rot.z:F4},{rot.w:F4}|" +
                                      $"{vel.x:F2},{vel.y:F2},{vel.z:F2}";

                byte[] bytes = Encoding.UTF8.GetBytes(statePayload);

                // Broadcast state payload to all active clients
                foreach (var ep in _clientEndpoints.Values)
                {
                    _udpListener.SendAsync(bytes, bytes.Length, ep);
                }
            }
        }
        #endregion
    }
}