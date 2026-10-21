# 03 szebs relay server

## Active scope — 2026-10-19

The owner explicitly requests **no TURN now and no owned server**. Implemented direct-only WebRTC using pinned werift in the bundled Node runtime, the public free PeerJS Cloud signalling helper and Google STUN. No provider account or administrator key is required. Public-helper/native tests use two processes on the same Windows PC, not two independent internet networks. There is no relay quota or paid fallback. Previous TURN/provider-entitlement work below is deferred, not a release requirement for this direct-only scope. The public helper has no confirmed SLA; failed routes remain without output. See ../PROTOCOL.md and ../../REMOTE.md.

## Goal

Provide the service integration for remote ShockWave clients within a EUR 0 recurring hosting budget. The initial goal is free connection setup and TURN fallback, not renting or building a paid relay.

## Work

- Evaluate Metered Realtime/Open Relay or another suitable free provider using current official documentation.
- Confirm the actual account allowance: the advertised 20 GB Open Relay offer differs from the general 500 MB TURN trial.
- Record message/concurrency/bandwidth limits, reset period, regions, and exhaustion behavior.
- Confirm the account can remain free; do not enable paid tiers, automatic top-ups, or billed fallback.
- Prove a WebRTC data channel in the project's packaged Windows runtime. WinForms and Node cannot be treated as browser WebRTC runtimes.
- Separate low-volume signalling from rapid RTCDataChannel traffic.
- Define a replaceable provider interface/configuration; do not hard-code administrator secrets into the app.
- Define invitation/credential issuance requirements with 04/05. A publishable service key is not toy-control authorization.
- Provide a mock provider and clear quota/unavailable states.
- Only introduce a small hosted service if the chosen authentication flow needs one and it fits the free budget.

No provider account credentials should be invented. If account access is needed, prepare the integration and record exactly what user-owned configuration is missing.

## Done when

- The chosen provider/runtime is documented with evidence.
- Actual entitlement is confirmed, or integration remains explicitly provisional.
- Direct and forced-relay connectivity can be tested with simulated payloads.
- No high-frequency stream uses a quota-counted messaging convenience API.
- Failure/quota paths are surfaced without silently spending money.
- TURN cutoff behavior is verified or marked unresolved.

## Starter prompt

Handle chat 03 szebs relay server. Read AGENTS.md and docs/lovense-remote first. Build the replaceable free signalling/TURN integration, confirm the actual free allowance and cutoff behavior, and prove WebRTC data channels in the native Windows/Node stack. Keep the recurring budget at EUR 0 and keep all physical output mocked. Document provider setup, remaining account requirements, and verification in STATUS.md.

## Next

[04 connecting clients](04-connecting-clients.md), with [05 owner controls](05-permissions-limits-stopping.md).

