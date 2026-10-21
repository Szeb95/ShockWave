# 05 permissions, limits, and stopping

## Current remote authorization — 2026-10-19

The owner explicitly chooses private-code pairing plus **Connect on** as authorization for supported connected toy functions and configured unpaused shockers. No additional per-device approval is required. Reconnect while both remain enabled reauthenticates with a fresh generation and zero output, without a second arming prompt. The earlier proposed separate-grant/paused-rearm UI below is superseded for this scope. Receiver-local caps, Test mode, owner override/Stop, short source expiry, authentication, malformed-input rejection and no retry/replay still apply. This implementation is software/public-helper tested with simulated hardware; physical failure/Stop behavior remains unverified.

## Active-output close warning (2026-10-11)

The app warns before any closing cleanup if a local toy has live/submitted/in-flight output or an unconfirmed stop, or a configured shocker has a bounded command/feedback window. Cancel is the default and keeps the app/input lifecycle intact; Close app uses existing stop/transport cleanup once. Connected idle devices, simulated output, Test mode previews and depth/position settings alone are excluded. New private Node `@activity` observations cover pending HTTP requests, accepted/rejected/ambiguous outcomes and remaining fresh API feedback, retaining deadlines across bridge exit until expiry. No device request, retry, saved setting or remote grant was added. Shocker commands may finish after quitting; physical stop is not measured. Native close/cancel/layout/in-flight/stop-failure fixtures and bridge HTTP/feedback regressions are recorded in STATUS.md. Real hardware validation remains pending.

## Local Toy control source ownership (2026-10-10)

The main-app avatar curve uses `LocalToyController.BeginInput/UpdateInput/EndInput` with runtime-only source GUID/generation and at most two seconds of lifetime. Fresh touch/Play claims a function; renewal requires that exact unexpired generation. Owner Input/Stop invalidates it, preventing stale input from undoing edits. Coupled motion Stop also invalidates related leases. Input is not forwarded to the unbounded owner Request path. Test mode, scan/disconnect/reset/errors retain existing gates and no replay; controller expiry closes a silent host independently of UI ticking.

Visible/alternating avatar heartbeat expires after two seconds; queued OSC over 250 ms old is discarded. Hide/avatar change/link failure ends VRC-owned leases while desktop input stays independent. Destination changes stop curve-owned local targets and require fresh input; Remote/Both cannot fall back to Own. These are local checks, not peer authentication or remote grants. Mock/UDP/fault evidence is in STATUS.md; physical stopping remains pending, including the existing limitation of blocked vendor calls or PC failure.

## Goal

Make the receiving owner authoritative over output and ensure stop/disconnect rules are applied consistently.

This work starts alongside 02 and must be verified before real remote output in 04.

Local page update (2026-10-08): the owner explicitly requested automatic connection/readiness and a held local value without manual Seconds/Run, then clarified that a separate Input slider must remain (0 off, 100 full request within saved limits). Healthy connections become ready with zero activity and no old replay. Test mode, limits, priority Stop, error closure and disconnect/reset cleanup remain. This local owner choice is not remote authorization: 04/05 still need per-source grants, generations, disconnect revocation and receiver freshness/watchdogs before any peer can provide output.

## Work

- Define grants for specific devices/capabilities and one active controller per capability.
- Require owner authorization before arming. Connection or room membership alone is insufficient.
- Enforce receiver-side maximum levels, bounded durations, pause, and test mode on every output route.
- Keep zero as off; stop bypasses minimum-active-level settings, cooldown, smoothing, and ordinary queue delay.
- Implement a local stop that closes the output gate without waiting for the network.
- Clear pending commands/patterns and invalidate the old output generation.
- Apply per-frame freshness plus a receiver-local watchdog owned by the hardware controller.
- Revoke immediately on owner action; reconnect/restart paused with no old activity replay.
- Handle adapter loss and app shutdown; document device-specific behavior on process/PC failure.
- Redact invitations, credentials, and private hardware identifiers in all diagnostics.

## Done when

- A sender cannot alter the owner's limits or target an unshared function.
- Test mode, pause, and revoke block all paths.
- Expired input stops continuous output while the local controller is alive.
- Old in-flight frames cannot restart output after stop.
- Remote and local failures are distinguished honestly; no guaranteed crash stop is claimed without hardware evidence.
- Mock fault cases exercise the actual output gate, not only UI settings.

## Starter prompt

Implement chat 05 permissions, limits, and stopping across the new local and remote toy paths. Read AGENTS.md and docs/lovense-remote. Centralize owner-side grants, limits, test mode, pause, stop, freshness, watchdog, and generation invalidation. Verify that stale traffic and reconnects cannot restart output. Preserve existing OpenShock command behavior and record mock versus hardware stopping evidence in STATUS.md.

## Next

Revisit during 04 and complete the release checks in 07.
