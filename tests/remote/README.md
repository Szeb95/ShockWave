# Remote connection checks

Local display-worker regressions verify frozen labels while the live model changes, one latest pending frame, retained Clear/display state, single-thread graphics ownership/disposal, immediate owner Stop during a blocked render, nonblocking shutdown and the existing pause/input-release behavior after a display failure. These default checks use fake graphics and simulated controllers.

Actual desktop mouse events also verify immediate Stop all before the first draw, ignored graph input for an undisplayed selection, accepted fresh input after drawing, and shock confirmation blocked until changed intensity/duration/armed state is displayed. These checks do not send a physical shock or toy command.

Add `--render-stress` separately to reproduce/control a slow display: a real isolated ToyControlPage, a 33 ms UI timer, simulated 180 ms graphics calls and a receiver input with 150 ms remaining. The synchronous drawing arrangement records expiry; the new graphics worker keeps UI updates/receiver renewals progressing. `work/remote-checks/render-performance.json` records observed gaps/work/frames/expiry. This opt-in throughput check has no shared-CI performance guarantee and changes no production deadline or network timestamp.

Add `--render-gpu` separately for the actual production Direct3D backend on its graphics thread, including frozen labels/state, a window swap chain, present and nonblocking page shutdown/resource cleanup. It writes `work/remote-checks/background-toy-panel.png` for visual inspection. Only an isolated offscreen window is shown; no normal app startup, settings, Bluetooth/device output or Unity is involved.

Inventory-removal regressions keep a local and two friend functions active, then remove one friend function without ticking the graph. Its row/lease must disappear immediately while the other inputs continue. Friend disconnect removes all remote rows, and returning inventory starts unselected at zero with no restored activity. This specifically covers a paused OSC/render timer; no real devices are used.

The 0.7.7 saved-friends checks cover isolated Windows encryption/reload, corrupt/failed storage preservation, key replacement refusal, rename/Forget and offscreen friend selection with Connect initially off. Node `remote-friends.test.mjs` covers both-side persistence acknowledgments, distinct private nonce derivation, failure, simultaneous saves, duplicate/malformed/expired requests and deterministic saved callers. No real user store is used.

To compile and check without creating an app EXE/package:

```powershell
dotnet build tests/remote/Remote.Checks.csproj -c Release -p:UseAppHost=false -p:PublishSingleFile=false -p:IncludeLegacyLovense=false
dotnet tests/remote/bin/Release/net8.0-windows/Remote.Checks.dll
dotnet tests/remote/bin/Release/net8.0-windows/Remote.Checks.dll --friends-internet
```

`--friends-internet` separately uses two isolated native sessions and source workers against the public setup helper: one Save on both apps, complete worker restart, code-free reconnection, one-side-off waiting, zero replay, fresh simulated control, Forget, rejection of the retained old key/original code, and fresh-code re-pairing. All identities/keys are generated only for these fixtures and kept out of logs/screenshots. Requires installed Node and restored bridge dependencies; both clients are on one PC, with simulated devices. This establishes neither separate-household reachability nor physical output/Stop timing. Saved friends require 0.7.7 or later on both apps.

The 0.7.6 suite also verifies expiry-reason logs, deduplication, numeric-only health summaries, private-field omission, untrusted-reason sanitizing, avatar-link diagnostics, a two-motor stopped graph returning to zero, and a small offscreen Log page with Copy log / Copy remote log / Clear. Clear resets visible/copied history; previews do not show the normal app form or start its devices.

Add `--log-stress` separately to run the opt-in performance reproduction: 500 long log entries, an isolated offscreen Log page, opening followed by repeated resizing/appending, and a real independently expiring 330 ms simulated receiver lease renewed by the UI timer. The old AllCells view expires that lease; the virtual fixed-height view stays active. Timing is measured locally rather than imposing a hardware-throughput requirement on shared CI. `work/remote-checks/log-performance.json` records show time, maximum timer gap and expiry; no normal app form, device, network peer, log attachment or real settings are used. Default checks cover full virtual values, bounded rotation, embedded spaces, copy and Clear.

Build and run on Windows with .NET 8. The default suite uses simulated toys and offscreen controls; it does not launch the normal app, access devices or start Unity.

```powershell
dotnet build tests/remote/Remote.Checks.csproj -c Release
./tests/remote/bin/Release/net8.0-windows/Remote.Checks.exe
```

Verify a complete published package using its actual bundled Node runtime:

```powershell
./tests/remote/CheckPublishedRemoteWorker.ps1 -PackageDirectory ./overlay/dist/ShockWave-0.7.6-win-x64
```

This checks that every restored runtime dependency is present with its expected hash, then starts only the Node connection worker. It verifies readiness, a valid private sharing code and clean disconnection. Invitations stay in memory and are never printed. Enabling the worker attempts the public setup helper, but code generation does not require that service to connect. Both build scripts run this check before reporting success or creating a release ZIP.

To exercise the real Connect button, code field and Copy availability against a package:

```powershell
$env:SHOCKWAVE_REMOTE_PACKAGE = (Resolve-Path ./overlay/dist/ShockWave-0.7.6-win-x64).Path
./tests/remote/bin/Release/net8.0-windows/Remote.Checks.exe --package-startup
```

The default suite also covers remote/SPS ownership handoff, live zeroes, a slow required stop, consecutive gestures, owner Stop between timer ticks, and fresh SPS recovery without reconnecting or changing assignments. It reproduces a timeout that clears selection, and verifies retained selection with cancelled output/loops, explicit Play, stale-result rejection, held desktop/avatar touch/pointer release gates, 152 ms RTT timing, bounded silence and worker-to-UI expiry. The Node peer suite separately uses actual encrypted peers with injected 150 ms RTT, jitter and packet loss; state has a 500 ms freshness bound and shock commands retain 250 ms. Add `--internet` to verify two native sessions through the public setup helper, four simulated toys in each direction, 30 seconds of sustained drawing with changing SPS contacts, independent sibling routing and zero-activity reconnection. Both clients still run on one Windows PC: these checks establish neither separate-household connectivity nor physical device response/Stop behavior.
