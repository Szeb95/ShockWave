# Remote connection checks

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
