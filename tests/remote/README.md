# Remote connection checks

Build and run on Windows with .NET 8. The default suite uses simulated toys and offscreen controls; it does not launch the normal app, access devices or start Unity.

```powershell
dotnet build tests/remote/Remote.Checks.csproj -c Release
./tests/remote/bin/Release/net8.0-windows/Remote.Checks.exe
```

Verify a complete published package using its actual bundled Node runtime:

```powershell
./tests/remote/CheckPublishedRemoteWorker.ps1 -PackageDirectory ./overlay/dist/ShockWave-0.7.4-win-x64
```

This checks that every restored runtime dependency is present with its expected hash, then starts only the Node connection worker. It verifies readiness, a valid private sharing code and clean disconnection. Invitations stay in memory and are never printed. Enabling the worker attempts the public setup helper, but code generation does not require that service to connect. Both build scripts run this check before reporting success or creating a release ZIP.

To exercise the real Connect button, code field and Copy availability against a package:

```powershell
$env:SHOCKWAVE_REMOTE_PACKAGE = (Resolve-Path ./overlay/dist/ShockWave-0.7.4-win-x64).Path
./tests/remote/bin/Release/net8.0-windows/Remote.Checks.exe --package-startup
```

The default suite also covers remote/SPS ownership handoff, live zeroes, a slow required stop, consecutive gestures, owner Stop between timer ticks, and fresh SPS recovery without reconnecting or changing assignments. It reproduces a timeout that clears selection, and verifies retained selection with cancelled output/loops, explicit Play, stale-result rejection, held desktop/avatar touch/pointer release gates, 152 ms RTT timing, bounded silence and worker-to-UI expiry. The Node peer suite separately uses actual encrypted peers with injected 150 ms RTT, jitter and packet loss; state has a 500 ms freshness bound and shock commands retain 250 ms. Add `--internet` to verify two native sessions through the public setup helper, four simulated toys in each direction, 30 seconds of sustained drawing with changing SPS contacts, independent sibling routing and zero-activity reconnection. Both clients still run on one Windows PC: these checks establish neither separate-household connectivity nor physical device response/Stop behavior.
