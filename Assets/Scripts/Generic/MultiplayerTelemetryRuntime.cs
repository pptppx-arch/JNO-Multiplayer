namespace Assets.Scripts.Multiplayer
{
    using System;
    using ModApi.Flight;
    using ModApi.Flight.Events;
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
        private IFlightScene _observedFlightScene;
        private bool _isShuttingDown;

        public static void EnsureCreated()
        {
            if (_instance != null) return;

            MultiplayerTelemetryRuntime existing = UnityEngine.Object.FindObjectOfType<MultiplayerTelemetryRuntime>();
            if (existing != null)
            {
                _instance = existing;
                return;
            }

            GameObject runtimeObject = new GameObject("[JNO Multiplayer Runtime]");
            UnityEngine.Object.DontDestroyOnLoad(runtimeObject);
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
            UnityEngine.Object.DontDestroyOnLoad(gameObject);
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

            // 5. ewuifewifunwi shutdown
            ObserveFlightScene();
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
            UnsubscribeFlightEnded();
            if (_instance == this) _instance = null;
        }
        private void ObserveFlightScene()
        {
            IFlightScene currentFlightScene = FlightSceneScript.Instance as IFlightScene;
            if (currentFlightScene == _observedFlightScene) return;

            UnsubscribeFlightEnded();
            _observedFlightScene = currentFlightScene;

            if (_observedFlightScene != null)
            {
                _observedFlightScene.FlightEnded += OnFlightEnded;
                Mod.Log("[MultiplayerTelemetryRuntime] Subscribed to flight-exit cleanup.");
            }
        }

        private void OnFlightEnded(object sender, FlightEndedEventArgs args)
        {
            ShutdownForFlightExit();
        }

        private void ShutdownForFlightExit()
        {
            if (_isShuttingDown) return;
            _isShuttingDown = true;

            try
            {
                bool wasHosting = ServerConnection.IsHosting;
                bool hadClientSession = ClientConnection.IsConnected
                    || ClientConnection.LocalClientId >= 0;

                // Stop sockets and telemetry before removing craft representations.
                if (wasHosting)
                {
                    ServerConnection.Stop();
                }

                if (hadClientSession)
                {
                    ClientConnection.Disconnect();
                }

                // ServerConnection.Stop/ClientConnection.Disconnect already queue registry cleanup.
                // Cancel older spawn work first so no stale proxy is created in the next scene.
                int cancelledWork = MultiplayerThread.CancelAllPending();
                Mod.Log($"[MultiplayerTelemetryRuntime] Flight ended; multiplayer shutdown complete. Cancelled queued work: {cancelledWork}.");
            }
            catch (Exception ex)
            {
                Mod.LogError($"[MultiplayerTelemetryRuntime] Flight-exit shutdown error: {ex.Message}");
            }
            finally
            {
                UnsubscribeFlightEnded();
                _isShuttingDown = false;
            }
        }

        private void UnsubscribeFlightEnded()
        {
            if (_observedFlightScene != null)
            {
                _observedFlightScene.FlightEnded -= OnFlightEnded;
                _observedFlightScene = null;
            }
        }
    }
}
