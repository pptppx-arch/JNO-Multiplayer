# JNO Multiplayer

**JNO Multiplayer** is an in-development multiplayer mod for **Juno: New Origins**. It uses a direct host/client model: TCP establishes a session and transfers craft XML, while UDP carries frequent flight telemetry. The current focus is a small, testable dogfighting foundation: each player has one visible kinematic proxy craft, and the host relays movement between connected players.

> **Current status:** the source contains the networking, craft XML, clock, telemetry, shutdown, and main-craft XML-refresh systems described below. It has **not yet been field-verified** with a reliable separate host/client test. “Implemented in source” does not mean “proven in a real multiplayer flight.”

## Quick test controls

Enter a Juno flight scene, then open Juno’s built-in development console. The commands below are registered by [`DevConsoleCommands.cs`](../Assets/Scripts/Generic/DevConsoleCommands.cs).

| Command | Purpose |
| --- | --- |
| `MP.Help` | Lists the multiplayer commands. |
| `MP.Status` | Reports whether the game is offline, hosting, or connected. |
| `MP.Host <port>` | Queues a local host after the current flight craft is ready. |
| `MP.Connect <host> <port>` | Queues a connection to a host after the current flight craft is ready. |
| `MP.Stop` | Requests a full multiplayer shutdown on the game thread. |

For example, a host can use `MP.Host 25555`, while a tester joins with `MP.Connect <host-ip> 25555`. Ports must be in the range **1–65535**. Starting or joining is intentionally delayed until Juno has built the local flight craft, so do not try to start the session from the designer or main menu.

## What the network does

| Layer | Direction | Current responsibility |
| --- | --- | --- |
| TCP | Client ↔ host | Connection handshake, client ID assignment, per-session UDP token, initial craft XML, updated craft XML, clock-sync messages, and disconnect notices. |
| UDP | Client → host → clients | Frequent PCI position, velocity, rotation, and angular-velocity telemetry. The client sends only its own craft; the host validates and relays current state. |
| Game-thread bridge | Network work → Juno | Defers craft spawn/destruction, XML capture, registry changes, and proxy movement to Juno’s game thread. |
| Clock | Host → clients | A host-owned monotonic tick supplies relay timestamps and supports the client clock estimator. |

The host is always **Client ID 0** and is identified by [`ServerConnection.IsHosting`](../Assets/Scripts/Multiplayer/ServerConnection.cs), not by creating a local `ClientConnection`. Each remote player is represented by one craft registered under that player’s client ID in [`CraftRegistry.cs`](../Assets/Scripts/Multiplayer/CraftRegistry.cs).

## Connection and craft flow

The client first opens TCP and sends `CONNECT`. The host allocates a client ID and a random per-session UDP token, then replies with `CONNECT_ACCEPTED`. Once the local craft is ready, the client sends compressed craft XML as `CLIENT_CRAFT_DATA`. The host validates/decompresses it, creates a kinematic host-side proxy, stores the XML for late joiners, replays existing craft XML to the joining client, and broadcasts the new client’s XML to already-active clients.

All Juno-facing work is owned by [`MultiplayerTelemetryRuntime.Update()`](../Assets/Scripts/Generic/MultiplayerTelemetryRuntime.cs). Socket continuations may receive and parse data, but they queue craft spawning and other Unity/Juno operations through [`MultiplayerThread`](../Assets/Scripts/Threading/MultiplayerThread.cs). This avoids calling Juno scene APIs directly from TCP or UDP continuation threads.

## In-flight main-craft XML refresh

Craft XML is no longer limited to the first connection. While a multiplayer session is active, the runtime reads the **one locally launched main craft** on the game thread every second, calculates a SHA-256 hash of its XML, and sends nothing when that hash is unchanged. The hash is memory-only; this feature does not write craft data to disk.

When the XML changes—for example, after an XML-visible staging or part-loss change—the owner sends `CLIENT_CRAFT_UPDATE` to the host. The host first accepts the normal bounded XML receive/spawn path, then replaces its cached XML and relays `UPDATE_CRAFT:<clientId>` to the other active clients. A receiver replaces the old kinematic proxy using the new XML. The replacement proxy keeps the previous visible transform until fresh UDP telemetry continues moving it, avoiding a visible jump from the temporary spawn position.

> **First-version boundary:** this synchronizes the player’s one main craft only. Detached boosters, debris, docking/undocking families, multiple independently controlled craft pieces, and collision physics are **not** synchronized yet. Remote crafts remain kinematic by design.

## Telemetry and proxy behavior

The UDP packet format is `TEL1`. It contains the owner client ID, host tick, sequence number, session token, PCI position and velocity, quaternion rotation, and angular velocity. The code uses Juno’s native double-precision position and rotation types while sampling and converting reference frames; Unity `Rigidbody` assignment occurs only at the final proxy application step.

[`HostTelemetryUpdater`](../Assets/Scripts/Telemetry/HostTelemetryUpdater.cs) is one host-wide relay, rather than one relay per client. It checks the TCP session state, source IP, session token, sequence freshness, numeric validity, and configured physical envelope before accepting a client packet. It can rebind an authenticated UDP **port** on the same IP address, which is useful for normal NAT port changes; it does not accept an IP-address change. The host owns the relay tick and relays the latest valid telemetry state to each client.

[`TelemetryReceiver`](../Assets/Scripts/Telemetry/TelemetryReceiver.cs) maintains bounded recent snapshots and applies interpolated or limited extrapolated movement to each remote proxy. Every remote proxy is set `isKinematic = true`; no remote Rigidbody collision simulation is currently used.

## Safety and lifecycle behavior

| Area | Current behavior |
| --- | --- |
| TCP writes | Each TCP connection uses one FIFO [`SerializedTcpWriter`](../Assets/Scripts/Threading/SerializedTcpWriter.cs), preventing overlapping writes on one stream. |
| Craft XML | Wire payloads and decompressed XML are bounded; the decompressed XML cap is **8 MiB**. Invalid Base64, GZip, UTF-8, or oversized XML is rejected. |
| UDP identity | Sessions use a 32-byte random URL-safe token. Token comparison is constant-time, and the host checks the expected TCP-derived source IP before accepting telemetry. |
| Telemetry input | Packets with an invalid field count, invalid token, non-finite values, stale sequence, invalid rotation, or unreasonable physical state are rejected. |
| Shutdown | `MP.Stop` and flight exit stop TCP/UDP work, dispose writers and sockets, cancel queued work, and destroy remote proxies while preserving the local Juno craft. |

## Testing reality and connection requirements

The current architecture is **direct hosting**. A real external test needs a reachable inbound TCP path for the handshake and craft XML, plus UDP for telemetry. A UDP-only STUN probe cannot make the required TCP listener reachable.

For early two-player testing, use a virtual-LAN application such as Tailscale and give the tester the host’s virtual-LAN IP. This avoids router forwarding while still exercising the mod’s own TCP, XML, and UDP paths. A successful virtual-LAN test proves the mod’s basic client/host flow, but it does not prove that UPnP, NAT traversal, or public direct hosting works on arbitrary routers.

| Test | What should happen |
| --- | --- |
| One-rig build check | The mod compiles, console commands register, invalid ports are rejected, and `MP.Stop`/flight exit clean up safely. |
| Virtual-LAN host/client | TCP handshake, initial craft XML, UDP telemetry, and kinematic remote proxy movement work without manual port forwarding. |
| Main-craft XML change | After a stage/part change, the remote main proxy is rebuilt from fresh XML and resumes moving from telemetry. |
| Bad token or wrong source IP | The host rejects the UDP packet and the proxy does not move from it. |
| Reconnect/disconnect | No ghost proxy remains after leaving, stopping, or losing a connection. |

## Known limits and next work

The project is not yet a finished public multiplayer service. It does not yet provide a rendezvous service, relay/TURN fallback, reliable no-port-forwarding connectivity on every network, multi-piece staging/debris synchronization, collision authority, host migration, large-lobby scaling, or final UI/diagnostics polish.

The immediate technical priorities are a clean in-game build, a successful virtual-LAN session with a separate tester, and observation of real proxy motion. Only after that test should the project tune interpolation/fixed-step behavior, optimize relay scaling for three or more clients, or expand in-flight craft changes into multiple independently networked pieces.

## Project documents

The fuller development checklist is in [`JNO_Multiplayer_PLAN.md`](JNO_Multiplayer_PLAN.md). The script-level transport diagram is documented in [`../Documentation/flowchart.md`](../Documentation/flowchart.md), with a rendered image at [`../Documentation/JNO_Multiplayer_Complete_Flowchart.png`](../Documentation/JNO_Multiplayer_Complete_Flowchart.png). The flowchart is useful for script relationships; this README is the authoritative high-level description of the currently implemented behavior.

## Development-only note

The optional remote debugger/helper is guarded by `JNO_MULTIPLAYER_DEV_REMOTE_DEBUGGER`. Do not define that symbol for a release build.
