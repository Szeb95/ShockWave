# Update checks

Build on Windows x64 with .NET 8, then run the fixture executable:

```powershell
dotnet build tests/update/Update.Checks.csproj -c Release
./tests/update/bin/Release/net8.0-windows/Update.Checks.exe
```

The suite uses fake HTTP/release metadata, real hashing/ZIP extraction, isolated file transactions, offscreen UI controls and simulated closing activity. It never shows/runs the normal production app, opens provider connections, connects to Bluetooth or starts Unity. Fixtures, fake settings, rollback backups and PNG previews stay in a unique directory under the test's ignored build output.

To verify an actual release ZIP without running its app:

```powershell
./tests/update/bin/Release/net8.0-windows/Update.Checks.exe --inspect-package ./overlay/dist/ShockWave-0.7.7-win-x64.zip 0.7.7
```

Verify the actual single-file helper against a fake installation:

```powershell
./tests/update/bin/Release/net8.0-windows/Update.Checks.exe --published-helper ./overlay/dist/ShockWave-0.7.7-win-x64/ShockWave.exe
```

The published-helper fixture uses numeric 0.7.6/0.7.7 manifests. It copies the real packaged EXE, invokes only `--apply-update`, and never requests a normal-app restart. It does not touch an installed app or personal data. For a later release, update the fixture's version pair explicitly.
