# Code map

Use this map to find a feature before reading the entire application. The settings and bridge split is a source reorganization; saved configuration, public bridge exports, JSON-line messages, output limits and retry/replay rules keep their existing meaning.

## Windows application

`overlay/OverlayForm.*.cs` files are parts of the same WinForms class. Shared fields and their existing initialization order remain in `OverlayForm.Shell.cs`; the extracted files contain methods, not additional initialized fields.

| File | Responsibility |
| --- | --- |
| `overlay/OverlayForm.Shell.cs` | Form construction, lifecycle, navigation, Toy panel composition and shared styling helpers. |
| `overlay/OverlayForm.Settings.cs` | Settings/Debug controls, self-test selection, load/save and mode banner. |
| `overlay/OverlayForm.Bridge.cs` | Node process startup/shutdown, stdout interpretation and API/OSC status presentation. |
| `overlay/OverlayForm.ActivityLog.cs` | Log-page layout, virtual rows, copying, redaction and session logging. |
| `overlay/OverlayForm.ShockerCards.cs` | Card arrangement, add/remove/save and card configuration/action messages. |
| `overlay/OverlayForm.OpenShock.cs`, `.PiShock.cs` | Existing provider-specific UI, synchronization and discovery. |
| `overlay/OverlayForm.LocalToys.cs`, `.Remote.cs`, `.ShockerControl.cs` | Existing integration between the shell and the separate device/remote/control components. |
| `overlay/OverlayForm.Window.cs`, `.Updates.cs` | Existing custom window behavior and updater coordination. |
| `overlay/BridgeConfig.cs`, `TokenStore.cs` | Persisted configuration/migration and encrypted credentials. |

Keep settings edits out of the device-output controller. Output gates, Stop, freshness and hardware scheduling stay in their existing controller/session classes. For local toys, remote or SPS work, first read `AGENTS.md` and the matching handoff documents under `docs/lovense-remote/`.

## Active OSC and panel rendering

The packaged app retains one `ToyOscRouter` UDP listener. It dispatches active
avatar-panel input through `VrcToyInput`, forwards local packets to the SPS page,
and forwards matching configured PiCtrl controls to the bridge's private stdin.
SPS presence and avatar numeric feedback remain active. The standalone Shader
Lab demo listener is not compiled into the app; `overlay/ToyControl/OscCodec.cs`
contains only the unchanged encoder/decoder and message type.

Desktop rendering uses `DesktopPanelView`/`DesktopPanelWorker`/`DesktopPanelCanvas`.
SteamVR uses `SteamVr/PanelView`, `RasterPanelWorker` and a persistent D3D11
bitmap-copy texture. Shared RAM recording/playback and avatar feedback are still
required. `tests/legacy-renderer/LegacyRenderer.props` compiles the historical
HLSL backend and assets only into the remote/toy check programs. Its shader
compiler dependency is absent from the application. The unused native preview
control and retired native button aliases are removed; live panel layouts and
OSC contracts are preserved.

## Node bridge

| File | Responsibility |
| --- | --- |
| `bridge/bridge.mjs` | Stable import/CLI entry point, provider dispatch and overlay JSON-line input. Existing named exports remain available here. |
| `bridge/bridge-config.mjs` | Legacy/provider normalization, validation, OSC address defaults and bounded control-request construction. |
| `bridge/openshock-api.mjs` | OpenShock HTTP requests/errors and log-event parsing. |
| `bridge/openshock-bridge.mjs` | OpenShock runtime, command/feedback handling, connection state and shutdown. |
| `bridge/pishock.mjs` | Existing PiShock provider implementation, receiving the same validation/request helpers. |
| `bridge/capture.mjs`, `osc.mjs` | Discrete avatar action capture and OSC encoding/decoding. |
| `bridge/live-logs.mjs`, `share-limits.mjs`, `shocker-names.mjs` | Existing live events and metadata/share operations. |
| `bridge/remote-*.mjs` | Existing direct-peer and saved-friend workers; separate from the provider process. |

New runtime files must also be listed as published content in `overlay/ShockWave.Overlay.csproj`. Verify the published bridge imports from the output folder: source checks alone cannot detect a missing packaged file. The bridge CLI remains `node bridge/bridge.mjs <configuration-file>`.

## Focused checks

Run these from the repository root on Windows with the existing locked dependencies restored:

```powershell
$bridgeTests = @(Get-ChildItem -LiteralPath bridge -Filter '*.test.mjs' | ForEach-Object FullName)
node --test @bridgeTests
dotnet build overlay/ShockWave.Overlay.csproj -c Release -p:IncludeLegacyLovense=false
dotnet run --project tests/shocker-cards/ShockerCards.Checks.csproj -c Release
```

Check each command's exit status before proceeding. The native card suite covers actual settings/save handlers, separate provider credentials/self-test choices, limits, redaction, close warnings and offscreen window rendering. Add `-- --toys-only` for Toy-panel/Settings composition checks; other focused switches are documented in `tests/shocker-cards/README.md`.

For changes crossing the separate feature boundaries, use the matching existing projects under `tests/local-toys/`, `tests/toy-control/`, `tests/remote/`, `tests/sps/` or `tests/update/`. Their guides describe isolated fixtures and any optional network/GPU checks. Use the existing publish/hidden-worker checks when changing packaging. None of these software results establish physical-device or uploaded-avatar behavior.
