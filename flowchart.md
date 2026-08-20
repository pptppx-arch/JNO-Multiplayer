# Juno Multiplayer — Complete Script Flowchart

This package documents the current `master` architecture of [JNO-Multiplayer](https://github.com/pptppx-arch/JNO-Multiplayer), reviewed at commit `7b6b94e`. It contains the **24 active C# scripts** under `Assets/Scripts` and shows their control, transport, game-thread, clock, craft, telemetry, and shutdown relationships.

> The rendered PNG is intentionally wide because it retains every script in one connected diagram. Use the Mermaid source when you want to zoom, edit labels, or split the system into focused diagrams.

## Reading the diagram

| Color group | Meaning |
| --- | --- |
| Blue | Bootstrap, persistent runtime, settings, and Designer UI |
| Gold | Host start, authoritative clock, and host connection manager |
| Green | Client connection and readiness flow |
| Purple | TCP framing, session, and serialized writer path |
| Red | Craft XML, game-thread spawn, registry, and Juno craft objects |
| Cyan | UDP packet, telemetry sender/relay/receiver, and proxy application |
| Gray | Game-thread dispatcher |
| Dashed red | Development-only remote debugger branch |

The diagram distinguishes two execution domains. **TCP and UDP continuations own sockets, framing, session state, and queued requests.** `MultiplayerTelemetryRuntime.Update()` owns game-thread work: draining `MultiplayerThread`, registering the ready local craft, capturing local craft XML, and applying telemetry to kinematic proxy objects.

## Script responsibility map

| Folder | Script | Responsibility | Principal relationships |
| --- | --- | --- | --- |
| `Generic` | `Mod.cs` | Mod bootstrap and centralized logging | Initializes toolbar UI and persistent runtime |
| `Generic` | `ModSettings.cs` | Empty extensible Juno settings category | Available to future user-configurable options |
| `Generic` | `MultiplayerJoinButton.cs` | Injects the Designer toolbar entry | Opens `MultiplayerJoinButtonDisplay` |
| `Generic` | `MultiplayerJoinButtonDisplay.cs` | Collects host/join input and begins flight | Starts `ServerConnection` or `ClientConnection`; invokes dev helper on join |
| `Generic` | `MultiplayerTelemetryRuntime.cs` | Persistent MonoBehaviour / game-thread owner | Pumps dispatcher, local craft registration, host clock, and client telemetry |
| `Generic` | `ServerClock.cs` | Host-authoritative monotonic fixed-step clock | Feeds `ServerConnection.PumpClock()` and `OnSimulationTick` |
| `Generic` | `ModHelper.cs` | Development-only remote debugger bridge | Optional join-side branch on port `4444`; not part of release architecture |
| `Multiplayer` | `ServerConnection.cs` | Host listener, sessions, handshake, craft exchange, broadcasts, cleanup | Owns `ClientSession`, `ServerClock`, host relay startup, and session removal |
| `Multiplayer` | `ClientConnection.cs` | Client TCP lifecycle, handshake, local XML upload, cleanup | Starts client telemetry after `CONNECT_ACCEPTED` |
| `Multiplayer` | `TcpNetworkSender.cs` | Initial client TCP connection and frame builder | Sends initial `CONNECT`; builds framing bytes |
| `Multiplayer` | `TcpNetworkReceiver.cs` | Shared framed TCP receiver | Feeds host and client receive loops with metadata/payload pairs |
| `Multiplayer` | `UdpNetworkHandler.cs` | Minimal shared UDP transport wrapper | Used by client updater, host updater, and receiver |
| `Multiplayer` | `PortForwarder.cs` | Attempts UPnP TCP and UDP port mapping | Called before host listener starts |
| `Multiplayer` | `CraftRegistry.cs` | Game-thread client-ID ↔ craft map and remote-proxy destruction | Used by spawner, telemetry, runtime, and queued cleanup |
| `Threading` | `MultiplayerThread.cs` | Bounded queue from network continuations to game thread | Pumped only by runtime `Update()` |
| `Threading` | `SerializedTcpWriter.cs` | One FIFO TCP writer for one connection | Owned by each server-side `ClientSession` and client outbound path |
| `CraftData` | `SendCraftData.cs` | Captures/compresses local craft XML and sends payloads | Invoked after local craft readiness |
| `CraftData` | `ReceiveCraftData.cs` | Decompresses received craft XML and queues a spawn | Dispatches `CraftSpawner` to the game thread |
| `CraftData` | `CraftSpawner.cs` | Creates a remote craft representation as a kinematic proxy | Uses Juno craft loader, flight scene, rigidbody, and registry |
| `Telemetry` | `TelemetryPacket.cs` | TEL1 double-precision wire packet serializer/parser | Shared by local packager, sender, host relay, and receiver |
| `Telemetry` | `LocalTelemetryPackager.cs` | Samples local Juno position, velocity, heading, and angular velocity | Produces a `TelemetryPacket` |
| `Telemetry` | `ClientTelemetryUpdater.cs` | Client UDP state sender and remote-proxy receive owner | Sends only local craft state to host |
| `Telemetry` | `HostTelemetryUpdater.cs` | One host-wide relay of latest client and host states | Validates/binds endpoints, assigns host tick, relays others' states |
| `Telemetry` | `TelemetryReceiver.cs` | Async UDP receive queue, sequence filtering, proxy smoothing | Applies transforms to kinematic remote proxies on game thread |

## Primary flows

| Flow | Script path |
| --- | --- |
| Host bootstrap | `Mod.cs → MultiplayerJoinButton.cs → MultiplayerJoinButtonDisplay.cs → PortForwarder.cs → ServerConnection.cs → ServerClock.cs` |
| Client bootstrap | `Mod.cs → MultiplayerJoinButton.cs → MultiplayerJoinButtonDisplay.cs → ClientConnection.cs → TcpNetworkSender.cs` |
| TCP handshake | `ClientConnection → TcpNetworkSender CONNECT → ServerConnection → ClientSession / SerializedTcpWriter → CONNECT_ACCEPTED → ClientConnection` |
| Craft exchange | `ClientConnection game-thread pump → SendCraftData → CLIENT_CRAFT_DATA → ServerConnection → SPAWN_CRAFT → ReceiveCraftData → MultiplayerThread → CraftSpawner → CraftRegistry` |
| Host craft replay | `ServerConnection → existing craft XML snapshots → joining ClientSession writer → SPAWN_CRAFT messages` |
| Telemetry uplink | `ClientTelemetryUpdater → LocalTelemetryPackager → TelemetryPacket → UdpNetworkHandler → HostTelemetryUpdater` |
| Telemetry relay | `HostTelemetryUpdater → host tick stamp + latest-state cache → UdpNetworkHandler → ClientTelemetryUpdater → TelemetryReceiver → CraftRegistry proxy` |
| Game-thread bridge | Network continuation → `MultiplayerThread.Post/Enqueue` → `MultiplayerTelemetryRuntime.Update` → Juno scene/registry operation |
| Shutdown | Connection stop/disconnect → updater/socket/writer cleanup → queued `CraftRegistry` proxy cleanup → Juno proxy destruction |

## Known diagram scope

The flowchart represents the repository implementation at the cited commit. It shows the active `TEL1` packet and currently implemented TCP/UDP roles. Proposed follow-up changes not yet committed to the repository—for example, the optional P1-2 per-session UDP token patch—are intentionally not depicted as active behavior.

## References

[1]: [JNO-Multiplayer repository](https://github.com/pptppx-arch/JNO-Multiplayer)

[2]: [Reviewed repository commit `7b6b94e`](https://github.com/pptppx-arch/JNO-Multiplayer/commit/7b6b94e34fa56cc3b233eeacf052d32217d6e4bb)
