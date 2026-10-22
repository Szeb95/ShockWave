# 04 connecting clients

## Immediate friend-toy removal (2026-10-22)

The owner requested that absent/disconnected friend toys disappear. A simulated inventory-removal fixture reproduced a retained control row when the drawing/OSC timer was not ticking. `RemoteToyController` now emits a local inventory event on replacement/disconnect; the graph session reconciles immediately and releases subscriptions on proxy changes/disposal. Removed rows/leases vanish without disturbing sibling remote or local input; returning rows start unselected at zero. Wire freshness/aliases, receiver limits/Stop, saved friend access and no replay are unchanged. SPS explicitly lists/configures/routes only local toys. STATUS records native/public-helper checks and the release; physical power-off detection still depends on the owner's backend reporting the device loss.

## Owner-approved connection policy — 2026-10-19

Both people run ShockWave. Private-code pairing and each person's **Connect** state authorize connected supported functions automatically, without per-toy or reciprocal-use approvals. Either person can disconnect/reconnect at any time; enabled peers retry automatically, restore fresh inventory and start with zero activity. The receiving app still enforces its limits/Test mode/Stop, peer authentication, freshness and no replay. This current user instruction supersedes the separate-grant/paused-rearm UI proposed below. Remote toys appear in Toy control; remote shocker inventory and discrete transport prepare the owner's later shocker tab. No TURN is implemented. See the implemented direct v1 section of PROTOCOL and STATUS for actual checks/remaining network and physical dependencies.

## Goal

Connect two ShockWave clients and support separately authorized sending/receiving through authenticated sessions.

## Dependencies

01 capability model, 02 local output adapter, 03 verified transport/provider, and 05 receiver gate. Use simulated hardware until the gate is verified.

## Work

- Implement invitations, expiry, intended-peer authentication, and connection identity binding.
- Show connecting, paired, paused/armed, direct/relayed, disconnected, and quota/unavailable states.
- Negotiate session-scoped channels and owner-controlled grants.
- Implement a fast live-state channel plus reliable acknowledged commands from PROTOCOL.md.
- Start at configurable 30-60 batched state frames/second; apply backpressure and keep only the newest pending snapshot.
- Reject unauthorized, malformed, duplicate, old-generation, and expired packets.
- Account for clock offset/uncertainty when checking freshness.
- Support both directions as independent grants; do not allow accidental forwarding loops.
- Reconnect with a fresh generation while paused; never replay missed activity.
- Report round-trip time/update age and adapter acceptance without claiming physical feedback.

## Done when

- Two local test instances exchange simulated multichannel state in both directions.
- Two separate internet clients work directly and through forced TURN where available.
- Owner permissions and limits remain authoritative.
- Delayed/reordered/duplicate packets cannot resume output after stop or reconnect.
- Quota exhaustion and network interruptions produce clear paused/failure states.
- Network frame rate and measured hardware rate are separately documented.

## Starter prompt

Implement chat 04 connecting clients using the project's actual adapter and transport decisions. Read AGENTS.md and docs/lovense-remote, especially PROTOCOL.md and 05. Add authenticated invitations, independent bidirectional grants, fast batched state, reliable commands, freshness/backpressure, and paused reconnection. Verify with simulated hardware first and update STATUS.md with direct/relayed internet evidence and remaining limitations.

## Next

[06 mappings](06-osc-sps-mappings.md) and [07 verification/packaging](07-testing-packaging.md).

