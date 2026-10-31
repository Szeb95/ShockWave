# Published-updater compatibility

`Legacy/` contains exact, test-only copies of the updater from public 0.7.0,
0.7.11 and 0.8.0 tags. Each provenance file records its source commit and SHA-256
hashes. Git preserves these bytes. Production does not reference these copies.

The default checks use fake release HTTP responses and isolated installation
files. They exercise the old release parser, download, manifest validation,
destination check and installer against the compatible layout. A sentinel old
root DLL and fake private stores must survive unchanged. Temporary installations
are removed on success; reports remain. No normal app, user settings, account,
toy, provider, Unity project or real friend is opened.

```powershell
dotnet run --project tests/update-compat/UpdaterCompatibility.Checks.csproj -c Release -p:LegacyVersion=0.7.11 -- --output work/legacy-updater-checks/0.7.11
./tests/update-compat/CheckPublishedCompatibility.ps1 -PackageZip ./overlay/dist/ShockWave-0.8.1-win-x64.zip
```

For a complete package, the upgraded app runs only its explicit read-only native
library diagnostic. This proves that it loads the pinned nested OpenVR DLL even
with a stale root DLL present. It does not initialize SteamVR or register input.

Optional `--old-helper <published ShockWave.exe>` invokes that exact old binary
only with `--apply-update` against the isolated installation. Restart is false
and the synthetic parent has already exited. `--online` additionally uses the
public GitHub latest-release API/download instead of fake HTTP; use it only
after that package is published. Evidence must distinguish fixture-only checks,
actual published-helper runs and public-download runs.
