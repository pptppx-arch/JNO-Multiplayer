# JNO Multiplayer Plan

**Current verified repository revision:** `5fedd6f` on `master`.

## Project rule

> Do not mark an item complete merely because the source exists. Mark it **source-complete** after review and **field-verified** only after host/client testing with a genuinely separate network participant.

When someone says a change is finished or pushed, first fetch `master` and inspect the resulting commit before updating this plan.

## Current status

| Area | Status | Notes |
|---|---|---|
| TCP framing, craft XML limits, serialized writers | Source-complete | Still needs connection testing. |
| Host identity, game-thread dispatcher, kinematic remote crafts | Source-complete | Still needs connection testing. |
| UDP token authentication and telemetry validation | Source-complete | Still needs bad-token and real relay tests. |
| P1 clock sync, snapshot playback, bounded telemetry work, handshake state | Source-complete | Requires build plus two-player test. |
| Flight readiness, port validation, development debugger gate | Source-complete | Build/UI test pending. |
| P2-2 host tick delta | Source-complete | Uses the instance-derived `_hostTickDeltaSeconds`. |
| P2-4 UI validation, P2-5 placeholders, P2-6 namespace import | Done in source | Verify in target mod build. |

## Next gate — build and single-rig checks

These do not require another computer.

- [ ] Rebuild the mod after the latest `Clock` namespace changes.
- [ ] Resolve every C# compiler error before changing networking architecture.
- [ ] Enter a flight scene and run `MP.Status`.
- [ ] Verify invalid UI ports are rejected: empty, `0`, `65536`, and non-numeric input.
- [ ] Verify `MP.Host <port>` and `MP.Connect <host> <port>` queue until a local flight craft exists.
- [ ] Verify `MP.Stop` and flight exit remove multiplayer state cleanly.
- [ ] Build once **without** `JNO_MULTIPLAYER_DEV_REMOTE_DEBUGGER`; confirm the remote helper is excluded.
- [ ] Build once **with** `JNO_MULTIPLAYER_DEV_REMOTE_DEBUGGER` only if the development remote helper is deliberately needed.

## Connectivity track — required before easy external testing

The current direct-hosted architecture needs an inbound TCP path for handshake/craft XML and a UDP path for telemetry. A UDP-only STUN probe does not make the TCP handshake reachable.

### C1 — Development test path

- [ ] Use a virtual-LAN solution for early testing so a volunteer can connect without manual port forwarding.
- [ ] Test using the virtual-LAN IP address, not public IP or router configuration.
- [ ] Record host and client logs for TCP connect, `CONNECT_ACCEPTED`, craft XML, first accepted UDP packet, and first remote proxy update.

### C2 — Product-grade no-port-forwarding path

- [ ] Specify a lightweight rendezvous protocol: room code, host registration, client join request, authenticated candidate exchange, expiry, and disconnect cleanup.
- [ ] Decide whether direct TCP is replaced by a relay-capable transport or whether the host must remain directly reachable for TCP.
- [ ] Add STUN candidate discovery and controlled UDP probe exchange only after the rendezvous protocol exists.
- [ ] Add a TURN-style relay fallback for networks where direct traversal fails.
- [ ] Add abuse controls: short room expiry, rate limits, opaque room IDs, authenticated relay allocations, payload caps, and relay bandwidth limits.
- [ ] Keep UPnP/NAT-PMP and manual forwarding as optional direct-host conveniences, not mandatory requirements.

## P2 — only after build is clean

### P2-1 — Relay scaling

- [ ] Replace the current `destination × craft` one-datagram relay loop with MTU-safe batched snapshots or interest management.
- [ ] Define a safe maximum datagram payload and split batches before fragmentation risk.
- [ ] Add proximity / same-body / launch-site interest filtering before implementing large lobbies.
- [ ] Test with at least three clients before calling this complete.

### P2-3 — Presentation timing and kinematic semantics

- [ ] Decide whether final proxy pose application belongs in `FixedUpdate`, a game fixed-step callback, or the current update pump after observing real remote motion.
- [ ] Keep snapshot velocity and angular velocity as telemetry/prediction data; verify whether writing Rigidbody velocity while kinematic has useful Juno behavior.
- [ ] Tune interpolation delay, snapshot history size, extrapolation cap, position smoothing, and rotation smoothing using measured packet loss/jitter.
- [ ] Add a stale-remote visual policy: freeze, fade, label, or despawn after defined silence thresholds.

## P3 — after the first successful real remote session

### P3-1 — Connection experience

- [ ] Add a connection-status UI: starting flight, waiting for craft, connecting TCP, exchanging craft XML, waiting for UDP, connected, disconnected, and error.
- [ ] Present actionable errors for invalid host, TCP timeout, token rejection, no UDP packets, and port-mapping failure.
- [ ] Add explicit reconnect and leave-session controls.
- [ ] Add a host session / room code display if rendezvous is implemented.

### P3-2 — Observability

- [ ] Add a development-only overlay or `MP.Diagnostics` command for ping/clock offset, host tick, packet loss estimate, send/receive rate, queue drops, interpolation delay, extrapolation count, and remote-craft count.
- [ ] Rate-limit repeated network warnings and expose counters instead of per-packet log spam.
- [ ] Add structured connection lifecycle logs with client ID and reason code.

### P3-3 — World and craft lifecycle

- [ ] Define explicit late-join synchronization: receive current crafts, then receive a current telemetry baseline, then become visible.
- [ ] Define respawn, craft destruction, vehicle replacement, and client reconnect semantics.
- [ ] Handle scene change / planet change / revert-to-launch consistently.
- [ ] Add host-side caps for craft XML size, active client count, remote craft count, and reconnect frequency.

### P3-4 — Security and resilience

- [ ] Add TCP and UDP per-client rate limits and temporary abuse backoff.
- [ ] Define token rotation / reconnect behavior and token lifetime.
- [ ] Audit every externally supplied string before using it as a log field, craft identifier, room code, or file/UI input.
- [ ] Keep all development shell, file-transfer, and debugger functionality behind development-only compilation symbols.

### P3-5 — Collision and authority research

- [ ] Do **not** implement collision-frame mediation until basic remote proxy flight is field-verified.
- [ ] Write a host-authority design for collision outcomes, including the exact PCI frame, tick, inputs, and deterministic state required.
- [ ] Decide whether remote craft collisions are cosmetic, host-authoritative, or unsupported in the first playable release.
- [ ] Prototype with two willing testers before expanding to prediction or reconciliation.

## Field-verification matrix

These require another Juno participant. A virtual LAN can satisfy this development-test requirement.

| Test | Expected outcome |
|---|---|
| Same virtual LAN host/client | TCP handshake, craft XML exchange, and UDP proxy motion succeed without router port forwarding. |
| Genuine external host/client | Same flow succeeds over the intended public connectivity method. |
| Bad token / wrong endpoint | Packet is dropped and proxy does not move. |
| Reconnect during craft exchange | No ghost proxy or unhandled exception. |
| Host/client `MP.Stop` and flight exit | Writers, sockets, telemetry, and remote proxies clean up. |
| UDP loss / pause | Interpolation remains stable; extrapolation stays bounded; stale state follows policy. |
| UDP source-port rebind | Authenticated same-IP session continues; changed IP is rejected. |
| Three or more clients | Relay scaling and interest/batching behavior stay within bandwidth and frame budgets. |

## Explicit non-goals for the first playable build

- Large public lobbies.
- Full collision mediation and reconciliation.
- Seamless host migration.
- Guaranteed direct connectivity on every network without a relay fallback.
- Release inclusion of the development remote helper.
