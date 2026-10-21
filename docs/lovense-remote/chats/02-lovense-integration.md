# 02 lovense into programm integration

## Shared scheduler response (2026-10-11)

The owner reports staggered Diamo/Hush/Max response and little improvement at 10 updates/toy/sec, and explicitly requests the recommended scheduler rework. Ready different toys now submit in groups of up to four, one normal call per toy. Calls remain serial through the existing native adapter/worker. Each successful normal call reserves 50 ms from submission time, with no idle credits; the next group waits for that allowance and a rolling window also caps normal submissions at 20/sec. Default 5/toy/sec and session 1–10 control, shared motor budgets, fair turns, caps/Test mode, generation/expiry guards and failure closure remain. Slow calls count toward pacing without a second pause; completed zero adds no new shared pause. Stop/expiry are revisited between each command. A final selection check prevents unchanged steps arriving after the preliminary scan from wasting budget. No backend, worker contract or Unity change; physical timing still needs the owner's retest. See LOCAL-TOYS and STATUS for verification/delivery.

## Quit cleanup fix (2026-10-11)

The user reported long closing waits alongside a disposed SPS query reset. `LocalToysPage` now synchronously disarms its controller, then begins controller/adapter and isolated transport cleanup together. The private `ILocalToyExit` hook forwards through the remembered adapter to the existing worker Dispose/quit path, interrupting normal command waits while retaining the zero attempt and bounded native cleanup fallback. Regular source/reset behavior, limits, persistence and IPC v1 remain unchanged. A hidden fake worker stalled inside a five-second output call exercises the actual page exit and checks disarming, exit under 2.5 seconds, worker termination and no replay. See STATUS for final checks/delivery; physical stop timing remains unverified.

## Main Toy control integration (2026-10-10)

The confirmed avatar demo has an authorized main-app route. Toy control sits below Toy panel and shares actual per-function inventory, RAM recording/playback and owner limits. Toy panel Input/Stop replaces only that function's VR value and cancels its loop; fresh touch/Play can claim again. Runtime source generations/expiry reject stale renewal after override/Stop/reset. Adapter, storage v1, Test mode, BLE scheduling and guarded no replay are retained. Numeric display feedback contains no hardware identifiers. Remote/Both are unavailable and SPS mappings remain separate work. See PROTOCOL.md v2 and STATUS.md for mock/scratch/package evidence; physical VR-to-BLE timing/stopping remains pending.

## Goal

Integrate native local Lovense controls into the existing Windows app.

Current owner-requested page flow (2026-10-08): connect discovered/remembered toys automatically, make healthy connected local controls ready, and provide separate Input/Sensitivity sliders, Max %, status and Stop for each function. The owner's latest clarification restores Input: 0 off, 100 full request within Sensitivity/Max %. Connect/Disconnect/Auto-connect/Enable/Seconds/Run remain removed. Local avatar Toy control OSC input is implemented; SPS and remote routes remain separate unfinished work. Its local controller holds a requested value until replaced/stopped/disconnected; legacy bounded manual duration stays in saved v1 data for compatibility. This supersedes the original manual page steps below, not future remote permissions/freshness/watchdogs or OpenShock behavior.

## Dependencies

UI follow-up (2026-10-11): toy and shocker names share the same font. Max % is a matching slider under Options; the compact per-function Stop button is removed. Input zero still clears that function; Stop toy in Options and Stop all toys in Settings retain their existing behavior. The owner withdrew model relocation, so model positions and manual shocker model labels remain unchanged. No controller, persistence, worker, bridge or shared wire/schema change.

UI update (2026-10-10): local function controls now live as compact three-column cards on **Toy panel**, matching shocker spacing. Name/function are top-left; Max % and function Stop top-right; Input/Sensitivity at the bottom; Options holds battery/read/forget/toy Stop. The separate Local toys navigation entry is removed. A second four-panel status row contains Bluetooth, the inactive future Ingame curve connection, click-to-toggle Scan toys and a clickable Reset Bluetooth panel. Source/reset/cancel/all-toy Stop and Updates/toy/sec moved to Settings. Explicit Stop scan pauses background discovery until the next manual scan/reset; owner output/controller/storage/worker contracts remain unchanged. The curve badge adds no OSC route.

01 device descriptors/adapter; [05 rules](05-permissions-limits-stopping.md) for test mode, limits, local stop, and expiry.

## Work

- Add toy cards and capability-specific controls using the existing app style.
- Start with vibration and independent motor selection; display only supported functions.
- Convert normalized values to native ranges; zero must stay off.
- Add bounded duration, local pause/stop, per-capability maximum, and adapter cleanup.
- Separate network update frequency from the native hardware scheduler.
- Persist settings with backward-compatible migration.
- Refactor both configuration validators so local Lovense does not require an OpenShock token.
- Extend structured IPC/status without breaking existing configuration/control messages.
- Keep secrets/identifiers out of examples and logs.
- Measure command rates on each available model; do not infer 60 Hz hardware support from 60 Hz networking.

## Done when

- A toy can connect, receive a bounded manual command, stop, and disconnect cleanly.
- Test mode blocks every new physical output path.
- Limits apply to manual and future mapped/remote inputs through one owner-side gate.
- Settings survive restart and legacy OpenShock settings still load.
- Missing hardware/SDK errors are actionable.
- Build/mock results and physical results are reported separately.

## Starter prompt

Implement chat 02 lovense into programm integration. Read AGENTS.md and docs/lovense-remote, including this brief, the device work from 01, and 05. Add native local Lovense toy cards, supported capability controls, bounded output, stopping, scheduling, and saved settings. Remove the global OpenShock-token dependency only where a local backend needs independence. Preserve existing OpenShock behavior and update STATUS.md with measured hardware limits and actual checks.

## Next

03/04 for remote integration; [06 mappings](06-osc-sps-mappings.md) once local manual controls are verified.
