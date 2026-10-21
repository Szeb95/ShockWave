# Bundled Bluetooth engine

Pinned official [Intiface Engine 5.0.4 Windows x64 release](https://github.com/buttplugio/buttplug/releases/tag/intiface-engine-5.0.4), published 2026-09-28. Source and data are from that exact tag; the former separate intiface-engine repository is archived.

- Archive SHA-256: `29EA7051B23F7A05124E65581DF48C8600070C83ADC0348F0738B9BA75B58782` (GitHub release digest, verified before extraction).
- EXE SHA-256: `06500F5B85D8534BAE845D83B231453B821B4F86EAECC461A34AF2C8407A93B7`.
- `lovense-devices.json` contains only the official v5 configuration's `lovense` protocol. Other brands, serial/dongle/gamepad managers, remote device servers, mDNS and REST are not enabled.
- Original release README and copyright/license are included. This is a local experimental ShockWave integration, not a separately installed or externally shared Intiface server.

ShockWave starts one hidden, owned child. The child connects to a random secret loopback WebSocket URL; ShockWave accepts one connection and closes the listener. Protocol v3 is negotiated explicitly, with a two-second ping watchdog. A Windows job terminates the engine if its owner exits. No raw device commands are exposed.

The engine's temporary `devices.json` provides the private identity/index association. It stays under a unique current-user temp directory and is deleted on normal shutdown; an OS crash can leave that directory. Addresses never enter project files, logs, public descriptors or remote grants. Only HMAC installation keys and existing encrypted remembered metadata persist. MAC-based keys reuse the legacy namespace when a Windows target MAC is available; other identity formats use a separate namespace and may require reassignment of old mappings.

Reported engine capabilities supply independent vibration motors, pump/constriction, rotation and oscillation ranges. Unsupported typed motion remains unavailable. Physical output/stop/driver timing is unverified by software fixtures.
