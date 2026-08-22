namespace Assets.Scripts.Multiplayer
{
    using System;
    using Assets.Scripts.Flight;
    using Assets.Packages.DevConsole;

    /// <summary>
    /// Local development-console controls for multiplayer testing.
    /// </summary>
    public static class DevConsoleCommands
    {
        private const int DefaultPort = 25555;
        private static bool _registered;

        public static void Register()
        {
            if (_registered) return;

            DevConsoleApi.RegisterCommand(
                "MP.Help",
                new Action(() => { Mod.Log(Help()); }));

            DevConsoleApi.RegisterCommand(
                "MP.Status",
                new Action(() => { Mod.Log(Status()); }));

            DevConsoleApi.RegisterCommand(
                "MP.Stop",
                new Action(() => { Mod.Log(Stop()); }));

            DevConsoleApi.RegisterCommand<int>(
                "MP.Host",
                new Action<int>(port => { Host(port); }));

            DevConsoleApi.RegisterCommand<string, int>(
                "MP.Connect",
                new Action<string, int>((host, port) => { Connect(host, port); }));

            _registered = true;
            Mod.Log("[MP Console] Registered.");
        }

        private static string Help()
        {
            return "MP.Status, MP.Stop, MP.Host, MP.Connect";
        }

        private static string Status()
        {
            string role = ServerConnection.IsHosting
                ? "hosting as Client ID 0"
                : ClientConnection.IsConnected
                    ? $"connected as Client ID {ClientConnection.LocalClientId}"
                    : "offline";

            string endpoint = ClientConnection.IsConnected
                ? $"; server={ClientConnection.hostIp}:{ClientConnection.TcpPort}"
                : string.Empty;

            int sessionCount;
            lock (ServerConnection.Sessions)
            {
                sessionCount = ServerConnection.Sessions.Count;
            }

            return $"JNO Multiplayer: {role}{endpoint}; sessions number: {sessionCount}.";
        }

        private static void Host(int port)
        {
            if (!TryValidateFlightAndPort(port, out string error))
            {
                Mod.LogWarning($"[MP Console] mp_host rejected: {error}");
                return;
            }

            if (ServerConnection.IsHosting || ClientConnection.IsConnected)
            {
                Mod.LogWarning("[MP Console] Stop the current multiplayer session before hosting.");
                return;
            }

            Mod.Log($"[MP Console] Starting host on TCP/UDP port {port}.");
            ServerConnection.Start(port);
        }

        private static void Connect(string host, int port)
        {
            if (string.IsNullOrWhiteSpace(host))
            {
                Mod.LogWarning("[MP Console] mp_join requires a host name or IP address.");
                return;
            }

            if (!TryValidateFlightAndPort(port, out string error))
            {
                Mod.LogWarning($"[MP Console] mp_join rejected: {error}");
                return;
            }

            if (ServerConnection.IsHosting || ClientConnection.IsConnected)
            {
                Mod.LogWarning("[MP Console] Stop the current multiplayer session before joining.");
                return;
            }

            Mod.Log($"[MP Console] Joining {host}:{port}.");
            ClientConnection.Connect(host, port);
        }

        private static string Stop()
        {
            if (!ServerConnection.IsHosting && !ClientConnection.IsConnected && ClientConnection.LocalClientId < 0)
            {
                return "JNO Multiplayer: no active session to stop.";
            }

            MultiplayerTelemetryRuntime.RequestShutdown();
            return "JNO Multiplayer: shutdown requested on the game thread.";
        }

        private static bool TryValidateFlightAndPort(int port, out string error)
        {
            if (FlightSceneScript.Instance == null)
            {
                error = "Enter a flight scene before starting or joining multiplayer.";
                return false;
            }

            if (port <= 0 || port > 65535)
            {
                error = "Port must be between 1 and 65535.";
                return false;
            }

            error = null;
            return true;
        }
    }
}
