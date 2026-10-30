# PiCtrl OSC ↔ OpenShock bridge

Developer navigation: [code map and focused checks](../docs/CODE-MAP.md). `bridge.mjs` remains the CLI/import entry point; configuration rules, OpenShock HTTP/log helpers and the OpenShock runtime now live in separate modules. Existing exports and message formats are preserved.

## PiShock provider — 2026-10-18

Login endpoint correction (2026-10-24): use `GET https://api.pishock.com/Account` with `X-PiShock-Api-Key`, as defined by PiShock's current OpenAPI `GetAccountEndpoint`/`GetAccountResponse` (`UserId`, `Username`). The old documented `auth.pishock.com/Auth/GetUserIfAPIKeyValid` lookup is removed and returns 404 even before discovery. The bridge uses the account's canonical username for the broker and identifies failed lookup stages in sanitized errors. No persisted credential/configuration, device command, retry or replay contract changes. The old prose documentation is insufficient for this account lookup; see [current schema](https://api.pishock.com/swagger/v1/swagger.json).

The bridge also supports **PiShock** through its documented account API and authenticated WebSocket v2 broker, with no separate application. `provider` defaults to `OpenShock` when missing. PiShock selects `piShockShockers`, `piShockSelfTestEnabled` and `piShockSelfTestShockerKeys`, with `piShockUsername` and the separate `PISHOCK_API_KEY` environment variable. Cards use numeric `hub/shocker[/share]` identifiers in `uuid`; share codes are discovered in memory, never saved in configuration or exposed over stdout. OpenShock's original fields, token context and command path remain intact. The packaged app supplies only the active service's secret. PiShock requires the existing bundled Node runtime (22+ for the built-in WebSocket); no new dependency was added.

PiShock uses the same discrete capture/limit rules, with 15-second provider maximum, returned share caps/permissions, no output queue and no command retry/replay. Metadata reads may refresh/reconnect; output requires a fresh account list and authenticated PONG. A broker reply must match the exact pending PUBLISH request before it is accepted. Accepted commands generate activity/OSC acknowledgement feedback; unrecognized live hub/log payloads are ignored rather than presented as confirmed physical telemetry. Hub state currently remains Unknown. Full setup, limits and hardware-check status: [PISHOCK.md](../docs/PISHOCK.md). Include `bridge/pishock.test.mjs` with the existing checks.

The packaged app receives private `@activity` stdout notices for its active-device closing warning: live requests in flight, accepted/rejected/ambiguous outcomes, and remaining fresh log activity or Stop. Notices include the configured card key, a fresh operation ID and a bounded duration; they contain no API token or hardware UUID. Preview commands in Test mode emit no notices. Fresh real API feedback can still warn, including other-controller activity. This adds observation only: command limits, cooldown, one-shot/no-retry behavior and OSC feedback remain unchanged. See [local notice contract](../docs/lovense-remote/PROTOCOL.md).

## Shared listener in the packaged app

ShockWave now owns one OSC listener for Toy control and PiCtrl. It starts the Node bridge with the private runtime flag `SHOCKWAVE_EXTERNAL_OSC=1`, then forwards matching PiCtrl packets over redirected stdin as `{ "kind": "osc", "packet": "<base64 OSC datagram>" }`. This mode does not bind a second socket. The existing discrete-action handler retains its fresh idle edge, capture window, per-device limits, cooldown and no retry/replay. The app drops queued packets older than 250 ms; feedback cannot become a toy action. Toy control uses separate `SzebsToy_*` inputs and `SzebsToy_Rx_*` numeric display feedback.

Standalone Node use retains its original exclusive OSC listener when that flag is absent. Private OSC IPC is accepted only in external-listener mode, bounded to one UDP-sized packet. It is not a public API, remote grant or persisted configuration field. Stop discards input; restarting requires fresh PiCtrl idle/action input. Toy control works without starting OpenShock; OpenShock requires Start bridge and its existing credentials/settings.

This PC program reads the PiCtrl avatar's three OSC parameters, sends commands to OpenShock, receives live OpenShock logs, and sends feedback parameters to VRChat. The live connection uses Microsoft's SignalR client. The published overlay includes its dependencies; for source use, run `npm ci --ignore-scripts` in the `bridge` folder once.

## 1. Prepare the avatar

Use the new `PiCtrlOscSenderSetupWindow.cs` generator from `work/PiCtrlEditor`, installed in the Unity project. In Unity **Edit mode**, select the avatar and run **Tools → ShockWave → PiCtrl → Create or Replace OSC Sender**, then save the scene. This creates `PiCtrl_OSC` under the avatar. The earlier Contact Sender and DATA/CLOCK files are no longer used by this bridge.

The bridge listens for these values from the local avatar:

| OSC address | Type | Value |
| --- | --- | --- |
| `/avatar/parameters/PiCtrl_Intensity` | Int | 0–100 intensity |
| `/avatar/parameters/PiCtrl_Duration` | Int | 0–100, where 100 means 15 seconds |
| `/avatar/parameters/PiCtrl_Action` | Int | 0 idle, 1 Shock, 2 Vibrate |

Enable OSC in VRChat's Action Menu. The bridge waits to see `PiCtrl_Action=0`, then captures intensity and duration when Action changes to 1 or 2. It allows 100 ms for the value messages to arrive and sends only one API request per idle-to-action transition. Unity Play mode does not itself emit VRChat OSC; test this with the VRChat client.

After starting the bridge, move both radials once so it receives their current values. If either value is missing, zero, or outside 0–100, it prints an ignored action. Releasing the action button must return `PiCtrl_Action` to 0 before another command is accepted.

For feedback on an avatar, add matching expression and animator parameters if you want to display the API log events:

| Parameter | Type | Meaning |
| --- | --- | --- |
| `PiCtrl_FeedbackEvent` | Bool | Brief pulse when a new OpenShock log appears |
| `PiCtrl_FeedbackActive` | Bool | Recent command active for its logged duration |
| `PiCtrl_FeedbackIntensity` | Float | Logged intensity divided by 100 |
| `PiCtrl_FeedbackMode` | Int | 1 vibrate, 2 shock, 3 sound, 0 stopped |

`PiCtrl_FeedbackActive` is inferred from a control log. It is not a live hardware state signal. Feedback names are separate from the sender's input names so API responses do not overwrite its controls.

## 2. Configure OpenShock

Create an API token in your OpenShock account and find your **shocker UUID**. In the native overlay, put the token in Settings and the UUID under Options on each shocker card. For command-line use, put each shocker in the `shockers` array in `bridge/config.json`, following `config.example.json`. Old single-shocker configuration files are still supported. Keep the token outside the file: set the `OPENSHOCK_TOKEN` environment variable in the terminal where you run the bridge. Do not share the token or commit it to Git.

In PowerShell, you can enter the token without placing it in your command history:

```powershell
$secureToken = Read-Host 'OpenShock API token' -AsSecureString
$env:OPENSHOCK_TOKEN = [pscredential]::new('OpenShock', $secureToken).GetNetworkCredential().Password
node .\bridge\bridge.mjs
Remove-Item Env:OPENSHOCK_TOKEN
```

The final line clears the token from that terminal session after the bridge exits. For a first OSC test, the token can be left unset while `dryRun` is true.

The sample config starts with `dryRun: false` and blank hardware UUIDs. Each shocker has `maxIntensity: 100` and `maxDurationMs: 15000`. Avatar duration is converted first: OSC 100 becomes 15,000 ms, OSC 20 becomes 3,000 ms. Each shocker's intensity and duration are then clipped to its individual minimum and maximum. Zero intensity or zero duration is ignored. Cooldown and pause are also individual and apply to both manual and avatar actions. Set `dryRun: true` for test logs without device commands. Existing explicit saved Test mode choices are retained by the app; live OpenShock output still requires credentials and Start bridge.

## 3. Run on this PC

Open PowerShell in this folder and run:

```powershell
node .\bridge\bridge.mjs
```

VRChat normally sends OSC to UDP port **9001**, where the bridge listens, and receives OSC on **9000**, where the bridge sends feedback. Only one app can listen on port 9001 at a time. This bridge binds to `127.0.0.1`, so it expects VRChat on the same PC.

To test without a shocker, leave `dryRun` true and use the new PiCtrl avatar menu in VRChat. The terminal should show one captured action when you press Shock or Vibrate. Then add the token and shocker ID, confirm that feedback appears, and enable live mode when ready.

For a PC-only test, leave the bridge running in test mode and run this in a second PowerShell window:

```powershell
node .\bridge\simulate.mjs Vibrate 10 20
```

That sends Intensity 10, Duration 20 (3 seconds), and a Vibrate action into the bridge. The first window should print `TEST ONLY`. It does not send a device command.

Run the local checks with:

```powershell
node --test .\bridge\bridge.test.mjs .\bridge\share-limits.test.mjs .\bridge\shocker-names.test.mjs
```

## Test your own devices

The overlay's **Settings → Debug → Control my own devices from VRChat** switch routes the selected VRChat parameter group to your own checked devices. For command-line configuration, use `selfTestEnabled`, `selfTestShockerKeys` (the selected cards' `key` values), and `selfTestAvatarGroup` (normally `PiCtrl`). The default is disabled. When enabled, only the selected devices receive avatar actions; normal card group routing resumes when disabled. This uses the existing OpenShock API connection and per-device limits, pause, and cooldown. `dryRun` still blocks real commands. API feedback is received normally and never loops into another device command.

## Implementation notes

Each shocker now has independent saved `manualMinIntensity`, `manualMaxIntensity`, `manualMinDurationMs`, and `manualMaxDurationMs` fields for owner card buttons and the explicit self-test OSC route. Missing fields inherit the previous `minIntensity`, `maxIntensity`, `minDurationMs`, and `maxDurationMs`, preserving existing selections and bounds. The original fields remain the normal avatar/other-controller limits. Pause, cooldown, ignored zero input, test mode, and no-retry/no-replay behavior remain shared. Normal OSC messages are not authenticated remote sessions, and self-test selection is not proof of a VRChat user's identity.

Local-only IPC `{kind:"applyLimits",key,requestId}` requests an owner operation. The bridge replies with `@limits` followed by `{kind:"share-limits",key,requestId,success,detail}`. The overlay requests this after a saved bottom-maximum edit is released and idle for 650 ms, or after clicking Apply. It never requests it mid-drag, for top/minimum-only edits, or automatically on startup/reconnection. Requests cannot overlap on a card; a new user edit during an in-flight request is coalesced into one later request for its latest values. The bridge preflights owned user shares and active public links, then PATCHes maxima while retaining tighter existing caps and command permissions. Public link cooldown is explicitly set from the card (none for zero, otherwise at least 300 ms); pause is untouched. Dry run blocks updates. Partial failures report counts and do not retry; without a new edit the user must click Apply. This operation requires owner/edit permissions and covers existing user shares/links only, not future shares, unclaimed codes or owner credentials. No control POST or other physical command is sent. API source checked against deployed OpenShock 3.18.0, commit `34c6d3ddf7d3cd5aab6b87e001a430f69a125469`.

Name synchronization adds local-only `{kind:"rename",key,name,requestId}` IPC, `@name {kind:"shocker-name",key,uuid,name}` metadata, and `@rename {kind:"shocker-rename",key,uuid,name,requestId,success,detail}` acknowledgement. `nameSyncPending` saves an explicit local rename intent; existing configs default false and use the OpenShock name on connection. This is owner metadata management, not a remote permission/output route. The bridge reads fresh shocker metadata and PATCHes `/1/shockers/{id}` with the desired name plus the current `device`, `rfId`, and `model`, as required by OpenShock's edit contract. Unknown/malformed pairing data aborts before PATCH. Equal names need no PATCH. Names are 1–64 characters; ownership and Shockers Edit permission are required. Dry run blocks writes. Changed target/name before PATCH cancels the old intent; failure is reported without automatic retry. Save options or a new bridge session can retry saved intent. In-flight writes already sent cannot be undone, and this GET/PATCH API provides no atomic guarantee against a simultaneous hardware-field edit in another app.

The bridge sends `POST /2/shockers/control` with OpenShock's V2 `shocks` format. It verifies each UUID with `GET /1/shockers/{id}`, then listens for `Log` and `DeviceUpdate` events on `/1/hubs/user` using the SignalR JSON protocol. Initial metadata supplies the name; a debounced GET after subscribing/reconnecting closes the subscription gap. Relevant ShockerUpdated notifications coalesce per card into a metadata read after 650 ms. Other hubs/types are ignored when the target hub is known. There is no repeating name/history polling. Validated UUIDs retain their state through slider saves and stream reconnections. Failed setup checks back off; HTTP 429 respects Retry-After. Name refresh failures wait at least the server delay (otherwise one minute) before allowing another event-triggered read; they do not schedule error retries. `pollIntervalMs` is retained for configuration compatibility and sets the minimum live reconnection delay. Device commands are never automatically retried. Events missed while disconnected or closed are not replayed. The first live round trip still needs an OpenShock account, a device, a receiving avatar, and VRChat to verify it.

References: [OpenShock API and authentication](https://wiki.openshock.org/dev), [OpenShock live user events](https://wiki.openshock.org/dev/signalr/user), [OpenShock's official SDK control request](https://github.com/OpenShock/SDK.Python/blob/master/openshock/resources/shockers.py), [VRChat OSC ports](https://docs.vrchat.com/docs/osc-overview), [VRChat avatar parameters](https://docs.vrchat.com/docs/osc-avatar-parameters).

## Hub telemetry

The existing SignalR client also receives `Welcome` and `DeviceStatus([{device,online,firmwareVersion}])`. Official deployed API source `34c6d3ddf7d3cd5aab6b87e001a430f69a125469` (`Common/Hubs/UserHub.cs`, `Common/Models/WebSocket/DeviceOnlineState.cs`, `API/Realtime/RedisSubscriberService.cs`) confirms that the first DeviceStatus after Welcome lists **all accessible online hubs only**; an empty initial list means none online. Subsequent events are incremental online/offline updates. The bridge validates UUIDs and boolean states, maps configured shockers through their existing metadata GET, and deduplicates hubs. Later empty lists do not erase cached state. No extra repeating metadata/history/hub polling or actuator request is added.

`@api` now includes `hub:{state,total,online,detail,shockers:[{key,state}]}`. Summary states are Online, Offline, Mixed, Checking, Unknown, TokenNeeded or UuidNeeded. Only hubs behind configured UUIDs count. Raw hub IDs remain inside the bridge. Stream interruption and Welcome invalidate the previous snapshot; reconnect needs new DeviceStatus data before reporting green. API health remains independent of hub online state. This telemetry is not evidence of shocker RF reception and does not retry or replay commands.

Include `bridge/hub-status.test.mjs` with the existing Node checks. It uses a fake HTTP/WebSocket server and no real account or physical output.

Local-only live configurations with no configured OpenShock UUIDs may validate without an OpenShock token. Configured live OpenShock output still requires its credentials. Local Lovense controls run directly in WinForms behind their own gate, with shared saved Test mode; they do not use the bridge's command IPC or create an OSC/remote toy route. No OpenShock physical command retry/replay behavior changes.
