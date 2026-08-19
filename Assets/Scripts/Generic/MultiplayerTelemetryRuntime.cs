namespace Assets.Scripts.Multiplayer
{
    using Assets.Scripts.Flight;
    using Assets.Scripts.Flight.Sim;
    using Assets.Scripts.Threading;
    using UnityEngine;

    /// <summary>
    /// Persistent game-thread owner for multiplayer work that is unsafe on TCP/UDP
    /// continuations. It registers the local craft only after a flight scene supplies one.
    /// </summary>
    public sealed class MultiplayerTelemetryRuntime : MonoBehaviour
    {
        private static MultiplayerTelemetryRuntime _instance;

        public static void EnsureCreated()
        {
            if (_instance != null) return;

            MultiplayerTelemetryRuntime existing = Object.FindObjectOfType<MultiplayerTelemetryRuntime>();
            if (existing != null)
            {
                _instance = existing;
                return;
            }

            GameObject runtimeObject = new GameObject("[JNO Multiplayer Runtime]");
            Object.DontDestroyOnLoad(runtimeObject);
            _instance = runtimeObject.AddComponent<MultiplayerTelemetryRuntime>();
        }

        private void Awake()
        {
            if (_instance != null && _instance != this)
            {
                Destroy(gameObject);
                return;
            }

            _instance = this;
            Object.DontDestroyOnLoad(gameObject);
            Mod.Log("[MultiplayerTelemetryRuntime] Persistent game-thread runtime created.");
        }

        private void Update()
        {
            // 1. Complete bounded craft spawn/despawn work requested by network tasks.
            MultiplayerThread.Pump();

            // 2. Register the local Juno craft after flight scene readiness.
            RegisterFlightReadyLocalCraft();

            // 3. Capture local XML only on the game thread after that registration exists.
            ServerConnection.PumpGameThread();
            ClientConnection.PumpGameThread();

            // 4. Advance time and apply queued UDP telemetry to proxy crafts.
            ServerConnection.PumpClock();
            ClientConnection.PumpTelemetry(Time.deltaTime);
        }

        private static void RegisterFlightReadyLocalCraft()
        {
            int localClientId = ServerConnection.IsHosting
                ? ServerConnection.HostClientId
                : ClientConnection.LocalClientId;

            if (localClientId < 0) return;

            FlightSceneScript flightScene = FlightSceneScript.Instance;
            CraftNode localCraft = flightScene == null
                ? null
                : flightScene.CraftNode as CraftNode;
            if (localCraft == null) return;

            if (CraftRegistry.GetCraft(localClientId) != localCraft)
            {
                CraftRegistry.RegisterCraft(localClientId, localCraft);
                Mod.Log($"[MultiplayerTelemetryRuntime] Local craft is ready for Client ID {localClientId}.");
            }
        }

        private void OnDestroy()
        {
            if (_instance == this) _instance = null;
        }
    }
}
