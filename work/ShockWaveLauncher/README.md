# Latest ShockWave launcher

This is the main-app counterpart to the preserved standalone panel launcher. `overlay/dist/00-Latest/Open Latest ShockWave.exe` validates the completed version's EXE hash and companion files, opens it in its own folder, or focuses that same version if already open. It does not close older apps, start SteamVR or enter the panel automatically.

After checking a new version, write its `build-info.json` and complete update manifest, then run `Update-Latest.ps1 -Version x.y.z`. It refuses to downgrade Latest. Version folders remain separate. `--resolve-only <file>` checks the destination without launching the app. The helper uses the Windows-included .NET Framework and needs no extra installation.

For 0.8.1 and later compatible packages, metadata is `docs/build-info.json` and native resources are under `ToyControl/SteamVR`. Older root-level packages remain supported. The stable desktop shortcut still targets the same launcher.
