namespace Assets.Scripts.Multiplayer
{
    using System;
    using ModApi.Flight;
    using ModApi.Flight.Events;
    using Assets.Scripts.Flight;
    using Assets.Scripts.Flight.Sim;
    using Assets.Scripts.Multiplayer.CraftData;
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
        private bool _pendingHostStart;
        private string _pendingJoinHost;
        private int _pendingStartPort;

        // XML is checked on the game thread. This does not write to disk; it prevents
        // unnecessary TCP XML messages and remote proxy replacement when nothing changed.
        private const float CraftXmlHashPollSeconds = 1.0f;
        private float _nextCraftXmlHashPollTime;
        private string _lastLocalCraftXmlHash;

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

            // 5. Check whether the one locally launched craft changed before sending a
            // replacement XML payload. This is intentionally periodic rather than physics-based.
            PollLocalCraftXmlForChanges();

            // 6. Observe scene transitions, then start only after Juno has built a local craft.
            ObserveFlightScene();
            TryStartPendingSessionWhenFlightReady();
        }

        /// <summary>
        /// Queues hosting until a flight scene and local craft are ready on the game thread.
        /// </summary>
        public static void RequestHostWhenFlightReady(int port)
        {
            RequestStartWhenFlightReady(null, port);
        }

        /// <summary>
        /// Queues joining until a flight scene and local craft are ready on the game thread.
        /// </summary>
        public static void RequestJoinWhenFlightReady(string host, int port)
        {
            if (string.IsNullOrWhiteSpace(host))
            {
                Mod.LogWarning("[MultiplayerTelemetryRuntime] Cannot queue a join without a host address.");
                return;
            }

            RequestStartWhenFlightReady(host.Trim(), port);
        }

        private static void RequestStartWhenFlightReady(string host, int port)
        {
            if (port <= 0 || port > 65535)
            {
                Mod.LogWarning("[MultiplayerTelemetryRuntime] Cannot queue a port outside 1..65535.");
                return;
            }

            EnsureCreated();
            if (_instance == null) return;
            if (ServerConnection.IsHosting || ClientConnection.IsConnected)
            {
                Mod.LogWarning("[MultiplayerTelemetryRuntime] Stop the active multiplayer session before starting another.");
                return;
            }

            _instance._pendingHostStart = string.IsNullOrEmpty(host);
            _instance._pendingJoinHost = host;
            _instance._pendingStartPort = port;
            Mod.Log($"[MultiplayerTelemetryRuntime] Queued " +
                $"{(_instance._pendingHostStart ? "host" : "join")} until local flight craft is ready.");
        }

        private void TryStartPendingSessionWhenFlightReady()
        {
            if ((!_pendingHostStart && string.IsNullOrEmpty(_pendingJoinHost))
                || ServerConnection.IsHosting
                || ClientConnection.IsConnected)
            {
                return;
            }

            FlightSceneScript flightScene = FlightSceneScript.Instance;
            if (flightScene == null || flightScene.CraftNode == null)
            {
                return;
            }

            bool startHost = _pendingHostStart;
            string joinHost = _pendingJoinHost;
            int port = _pendingStartPort;
            ClearPendingStart();

            if (startHost)
            {
                ServerConnection.Start(port);
            }
            else
            {
                ClientConnection.Connect(joinHost, port);
            }
        }

        private void ClearPendingStart()
        {
            _pendingHostStart = false;
            _pendingJoinHost = null;
            _pendingStartPort = 0;
        }

        /// <summary>
        /// Periodically hashes the main locally launched craft XML. A changed hash causes one
        /// reliable TCP XML update; unchanged XML causes no network message or proxy rebuild.
        /// Detached debris is intentionally outside this first implementation's scope.
        /// </summary>
        private void PollLocalCraftXmlForChanges()
        {
            bool hosting = ServerConnection.IsHosting;
            bool connected = ClientConnection.IsConnected;
            if (!hosting && !connected)
            {
                _lastLocalCraftXmlHash = null;
                _nextCraftXmlHashPollTime = 0f;
                return;
            }

            if (Time.unscaledTime < _nextCraftXmlHashPollTime)
            {
                return;
            }

            _nextCraftXmlHashPollTime = Time.unscaledTime + CraftXmlHashPollSeconds;
            if (!SendCraftData.TryGetLocalCraftXmlCompressedAndHashOnGameThread(
                    out string compressedXml,
                    out string contentHash))
            {
                return;
            }

            // The initial handshake already sends a complete craft snapshot. Establishing this
            // baseline prevents an immediate duplicate update right after joining or hosting.
            if (string.IsNullOrEmpty(_lastLocalCraftXmlHash))
            {
                _lastLocalCraftXmlHash = contentHash;
                return;
            }

            if (string.Equals(_lastLocalCraftXmlHash, contentHash, StringComparison.Ordinal))
            {
                return;
            }

            bool queued = hosting
                ? ServerConnection.UpdateHostCraftXmlOnGameThread(compressedXml)
                : ClientConnection.QueueLocalCraftXmlUpdateOnGameThread(compressedXml);
            if (queued)
            {
                _lastLocalCraftXmlHash = contentHash;
                Mod.Log("[MultiplayerTelemetryRuntime] Local craft XML changed; queued refresh.");
            }
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

        /// <summary>
        /// Stops the active host or client session from a game-thread caller, including all
        /// telemetry, TCP writers/sockets, queued multiplayer work, and remote craft proxies.
        /// </summary>
        public static void RequestShutdown()
        {
            EnsureCreated();
            _instance?.ShutdownMultiplayer("MP.Stop", false);
        }

        private void OnFlightEnded(object sender, FlightEndedEventArgs args)
        {
            ShutdownMultiplayer("flight exit", true);
        }

        private void ShutdownMultiplayer(string reason, bool unsubscribeFromFlight)
        {
            if (_isShuttingDown) return;
            _isShuttingDown = true;

            int preservedClientId = ServerConnection.IsHosting
                ? ServerConnection.HostClientId
                : ClientConnection.LocalClientId;

            try
            {
                ClearPendingStart();
                _lastLocalCraftXmlHash = null;
                _nextCraftXmlHashPollTime = 0f;

                // Discard old spawn and proxy work before shutting down. Otherwise it can run
                // after cleanup and recreate a remote proxy in the next frame or scene.
                int cancelledBeforeShutdown = MultiplayerThread.CancelAllPending();

                if (ServerConnection.IsHosting)
                {
                    ServerConnection.Stop();
                }

                // Disconnect is idempotent and also handles a TCP connection that exists before
                // CONNECT_ACCEPTED assigns LocalClientId.
                ClientConnection.Disconnect();

                // This method runs from Juno's game-thread lifecycle or dev-console callback.
                // Clear synchronously so MP.Stop and flight exit cannot leave remote proxies behind.
                CraftRegistry.ClearAllExcept(preservedClientId);

                // Stop/Disconnect schedule their normal asynchronous cleanup too. It is now
                // redundant and unsafe after a rapid reconnect, so discard it with any late work.
                int cancelledAfterShutdown = MultiplayerThread.CancelAllPending();
                Mod.Log($"[MultiplayerTelemetryRuntime] {reason}: multiplayer shutdown complete. " +
                    $"Cancelled queued work: {cancelledBeforeShutdown + cancelledAfterShutdown}.");
            }
            catch (Exception ex)
            {
                Mod.LogError($"[MultiplayerTelemetryRuntime] {reason} shutdown error: {ex.Message}");
            }
            finally
            {
                if (unsubscribeFromFlight)
                {
                    UnsubscribeFlightEnded();
                }

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
