# 07 testing and packaging

## Goal

Verify the complete experience and ship it as one ShockWave installation on each Windows client.

## Work

- Build a simulated toy/adapter and two-client harness early.
- Exercise sustained batched updates with multiple channels, direct transport, and forced relay.
- Inject delay, jitter, loss, duplicates, reordering, congestion, disconnects, and reconnects.
- Exercise rejected invitations, permission revocation, malformed payloads, incompatible versions, expired frames, and quota failures.
- Measure median and tail update age/latency, queue pressure, actual relayed usage, and separate hardware command rates.
- Verify that stop/disarm cannot be undone by old state and patterns do not replay.
- Check regressions in current OSC/OpenShock controls, saved settings, token handling, and test mode.
- Package the selected native Lovense and WebRTC dependencies with licenses and required architecture/runtime files.
- Verify a clean Windows installation with usable Bluetooth and no separate Lovense Remote or Intiface app.
- Produce a short user guide for discovery, invitations, permissions, stop, connection failures, and free-service limits.
- Keep any real provider/hardware claims tied to recorded evidence.

## Done when

- Required automated/build checks pass.
- Physical connection, bounded output, stop, and sustainable rate are demonstrated on the listed tested models.
- Two internet clients work with the supported direct/relay modes.
- Free-account usage and exhaustion evidence is recorded.
- The published app includes its needed native/transport dependencies.
- Known limitations and runtime requirements are visible to users.
- Startup, reconnect, and test mode do not accidentally activate devices.

## Starter prompt

Handle chat 07 testing and packaging. Read AGENTS.md and docs/lovense-remote, build meaningful adapter/session fault checks, measure sustained network and hardware rates, preserve existing OpenShock behavior, and package one ShockWave app for both ends. Verify on a clean Windows setup where possible. Record actual hardware/provider evidence, quotas, required runtimes, and unresolved limitations in STATUS.md before describing it as release-ready.

