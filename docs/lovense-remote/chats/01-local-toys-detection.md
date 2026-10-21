# 01 local toys detection

## Goal

Make supported local Lovense toys discoverable inside ShockWave without requiring standalone Lovense Remote or Intiface.

## Read first

[Roadmap](../README.md), [architecture](../ARCHITECTURE.md), [draft protocol](../PROTOCOL.md), [status](../STATUS.md), and the current overlay/bridge source.

## Work

- Investigate the native Windows Lovense SDK/BLE library, credential requirements, and redistribution.
- Define a shared adapter/device/capability descriptor with stable local keys.
- Add scan/start/stop, connection state, model/functions, and battery where supported.
- Keep scanning off the UI thread; handle Bluetooth unavailable, scan cancellation, disappearing toys, and duplicate discovery.
- Keep vendor identifiers local and remote aliases separate.
- Supply a simulated adapter so later chats can proceed without physical toys.
- Use the existing native interface and IPC conventions where appropriate.

This chat handles inventory and connection visibility. Physical activation belongs to 02.

## Done when

- Discovery does not activate a toy.
- Start/stop scan and adapter cleanup work repeatedly.
- Multi-motor/functions are represented independently.
- Mock checks cover missing Bluetooth, duplicates, disconnects, and unknown capability/battery.
- Real discovery is demonstrated on available hardware, or explicitly recorded as unverified.
- Descriptor changes are recorded for 02 and 04.

## Starter prompt

Implement chat 01 local toys detection in this project. Read AGENTS.md and docs/lovense-remote, especially this brief, before editing. Add native Windows Lovense discovery and a simulated adapter, define the shared capability model, and keep this stage from activating hardware. Preserve existing OpenShock behavior. Record actual verification and unresolved native SDK/model requirements in STATUS.md.

## Next

2026-10-11 follow-up: automatic-all discovery connects fresh candidates after a short bounded advertisement batch rather than waiting for a complete 15-second collection/all offline saved targets. Fresh manual candidates connect before another search. Startup-only collection retains its budget; a short search that misses a known toy extends subsequent searches to a full uninterrupted window while output is idle, preserving healthy connections. Empty/missing attempts retry after three seconds. Explicit scans, Stop pause/cancellation, worker serialization, output gates and persistence remain. See LOCAL-TOYS.md and STATUS.md for exact local timing and mock versus physical evidence.

[02 Lovense integration](02-lovense-integration.md).

