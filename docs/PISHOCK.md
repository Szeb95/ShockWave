# PiShock in ShockWave

PiShock connects directly from ShockWave to PiShock's cloud API. No additional desktop program is required. The PC and powered PiShock hub need internet access. The website is used for initial hub setup, API-key creation and account sharing; it does not need to remain open while ShockWave runs.

## Connect

1. Generate an API key on your PiShock account page. The broker requires a key generated after 2024-10-15; create a new one if an old key is rejected.
2. In **Settings → Shocker service**, select **PiShock**.
3. Enter your **PiShock username** and **API key**. **Remember PiShock key on this PC** encrypts the key for your Windows account. Select **Save settings**.
4. On **Toy panel**, select **Start bridge**. Wait for **PiShock API → Connected**.
5. Select **Add shocker**, choose an available device, and confirm **Add shocker**. Your own devices and devices already shared with your account are listed. Claim another person's share code on PiShock's website first; the app does not claim codes or alter shares automatically.
6. Set the card's intensity/duration limits and cooldown. Use **Settings → Debug → Test mode** to preview commands without sending device operations. In live mode, a button press sends one operation to its selected device.

**Refresh devices** in PiShock Settings reads the list again while the bridge is connected. Automatic metadata refresh is once per minute. A failed refresh blocks commands until it recovers. Unavailable/expired device information is never used to queue output for later.

## Controls and compatibility

- **Shock**, **Vibrate** and **Sound** map to PiShock shock, vibration and beep. Sound has no adjustable strength on PiShock.
- The upper ranges apply to your card buttons and explicit self-test. The lower **Limits for others** ranges apply to avatar commands routed through this app. Pause and cooldown apply to both. PiShock's service/share permissions and tighter returned caps also apply. This integration bounds every operation to at most 15 seconds; shorter durations use milliseconds.
- **Options** identifies a target as `hubId/shockerId` or `hubId/shockerId/shareId`. These are numeric IDs, not a share code or secret. Normally select devices through **Add shocker** rather than typing IDs. The same physical shocker cannot be assigned twice through different shares.
- Names/model labels save locally. Account renaming and editing share/link limits remain on PiShock's website. There is no **Apply to OpenShock** button on a PiShock card. Local lower limits do not restrict commands sent by somebody using another app.
- Existing discrete PiCtrl avatar actions work through the selected service. Default groups are `PiCtrl`, `PiCtrl_2`, etc. Existing custom addresses remain supported. **Control my own devices from VRChat** and its selected devices are saved separately for OpenShock and PiShock. This integration does not add continuous shocker curves or SPS shocker routing.
- The four top panels retain their positions. **PiShock API** reports account/broker access. **PiShock Hub** currently reports **Unknown** for configured targets because the public API documentation does not specify a validated live hub payload. A broker PONG or accepted command is not proof that the hub/shocker is powered or that physical output occurred.
- Accepted commands appear in the Log and generate bounded activity/OSC feedback. Feedback is the server's acknowledgement, not a physical measurement. Unrecognized live hub/log messages are ignored, with one generic diagnostic per connection; other controllers' activity is not currently decoded.

Selecting **OpenShock** again restores its original cards, encrypted token and self-test selection. The two providers are alternatives, not simultaneous outputs. Saving a service/connection change restarts the running bridge and clears captured input. Fresh avatar interaction is required after reconnecting. Settings and legacy OpenShock token encryption remain compatible.

## Failures and stopping

Device commands are sent once on an authenticated connection. Rejected, timed-out or disconnected commands are never retried, replayed or sent through a fallback transport. Additional presses while a command acknowledgement is pending are ignored rather than queued. A lost acknowledgement is reported as **outcome unknown**. Metadata reads and the socket may reconnect without resending output.

**Pause** and **Stop bridge** block new app commands. An already accepted operation can continue for its requested duration; stopping the app is not a confirmed PiShock emergency stop. Use PiShock's own controls for stopping an operation. The existing app-close warning observes pending, accepted and uncertain app operations.

The API key is stored separately in `%LOCALAPPDATA%\SzebsToys\pishock-api-key.bin`, using Windows account encryption. OpenShock retains `api-token.bin`. Keys are passed only to the selected bridge process through its environment and are absent from JSON configuration. Share codes returned by PiShock stay in bridge memory and are excluded from device-list IPC and logs. Never share either credential file or your personal settings/log directory.

## Verification

The local HTTP/WebSocket fixtures exercise owned/shared discovery, permissions and limits, command acknowledgement matching, failure/timeout/disconnect handling, metadata throttling/recovery, OSC routing, Test mode, and no retry/replay. Native fixtures check provider switching, separate settings/credentials, encryption, local naming/limits, status panels and rendered cards/settings. Existing OpenShock tests remain in place.

Run from the repository root:

```powershell
node --test bridge/pishock.test.mjs bridge/bridge.test.mjs bridge/share-limits.test.mjs bridge/shocker-names.test.mjs bridge/hub-status.test.mjs bridge/toy-osc.test.mjs
dotnet run --project tests/shocker-cards/ShockerCards.Checks.csproj -- --pishock-only
dotnet run --project tests/shocker-cards/ShockerCards.Checks.csproj
```

No real PiShock account, physical output or actual broker round trip was tested during implementation. The owner's friend will verify their own setup. Confirm account connection and device listing first, then the chosen modes, intensity/duration, account pause/permissions and recovery without stale output. Report only the displayed error and necessary redacted context; do not send an API key or share code. No EXE/installer release was packaged for this source change, at the owner's request.

The owner subsequently requested a runnable version on 2026-10-18. A separate self-contained Windows x64 portable package is now available in `overlay/dist/shockwave-pishock-20261018/`, with a ZIP alongside it for sharing. Extract the entire ZIP, then run `ShockWave.exe`. It includes .NET 8.0.31, Node 24.21.0 and the existing default Bluetooth engine; no separate runtime installation is needed. Close any previous ShockWave instance normally before opening it. Existing personal settings are reused on the same Windows account; they are not included in the ZIP. The packaged OpenShock/PiShock preview startup and isolated EXE worker checks pass. Physical PiShock verification remains outstanding.

Primary references: [PiShock account/device API](https://docs.pishock.com/pishock/api-documentation/pishock-api-documentation.html), [WebSocket API](https://docs.pishock.com/pishock/api-documentation/websocket-api-documentation.html), [PiShock hub and sharing guide](https://docs.pishock.com/pishock/pishock-v3-documentation.html).
