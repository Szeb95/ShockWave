# Updating ShockWave

Open **Update** below **Settings** in the left navigation.

- **Current version** identifies the running copy.
- **Check for updates** checks the latest published stable release in `Szeb95/ShockWave`. Normal app startup and an hourly background check do this too.
- **Update** downloads a compatible new version, verifies it, closes normally and installs/restarts it. The button stays disabled until a compatible newer release is found.
- **Automatic update** defaults off. Turning it on saves the preference for this PC. Available versions download automatically and install when you next close ShockWave normally. Automatic installation does not reopen the app.
- **View published version on GitHub** opens the detected release, or the repository's latest-release page before one has been detected.

When a compatible update is available, **Update available** appears beside the **Toy panel** heading. Click it to open Update.

## First installation and saved data

Install the first updater-enabled complete Windows ZIP manually: close the previous copy normally, extract the whole archive into a new folder and run its `ShockWave.exe`. Version 0.7.0 introduces this updater. Developer builds and older packages without the ownership manifest must use the complete-release download first; the app reports this instead of guessing which existing files it may replace.

Credentials, device limits, mappings and Test mode stay in their existing `%LOCALAPPDATA%\SzebsToys` files. The new automatic-update preference uses `update-settings.json` separately. Download staging and recovery backups are under `SzebsToys\updates`; they contain program files, not a copy of personal settings.

Existing active-device close confirmation remains in force. Cancel keeps the app and its controls running. Normal shutdown disconnects/stops through the existing paths before the helper proceeds; an already accepted shocker command can continue for its bounded duration. Restart does not restore previous continuous input or replay commands.

The updater operates on the running program's folder. It never installs tools into an existing Unity project, opens/saves scenes or uploads an avatar. Updated optional editor sources remain package files for the user to import and apply explicitly.

## Availability and errors

The public release location is `https://github.com/Szeb95/ShockWave/releases/latest`. If the repository has no public release yet, **No public release is available yet** is expected. If the latest release matches your installed version, the app reports that you have the latest published version. Offline requests and rate limits show an error status; failed downloads do not close the app or change its installed files.

Another running copy or worker can prevent installation. The updater does not forcibly close it. User-added files that collide with incoming package paths cause installation to stop rather than overwrite them. On a copy failure it attempts to restore all replaced files; if restoration also fails, the helper reports the retained backup location. Power loss or externally locked files can still require manually extracting a complete release into a new folder.

## Release contract and verification

Publisher instructions:

```powershell
./overlay/package-release.ps1 -Version 0.7.2
```

Use a new numeric version for each future release. The command refuses an existing output path and builds `ShockWave-VERSION-win-x64.zip`, its SHA-256 sidecar and a complete package folder. It includes pinned .NET/Node/Bluetooth dependencies, notices, the project license, user guides and optional editor source. It excludes private settings, proprietary legacy BLE DLLs and debug files. It verifies all external Node runtime dependencies and sharing-code generation, as well as DLL-free hidden-worker startup checks.

Publish the ZIP as a release asset at a matching `vVERSION` or `VERSION` stable tag in the intended repository. The **Prepare Windows release** GitHub workflow packages/checks a version and creates a draft; the owner publishes the finished release. Drafts/prereleases are not offered by the app. The release workflow itself still needs its first GitHub-hosted run after the repository exists.

The updater accepts only the intended repository's compatible ZIP asset, using HTTPS and an allowlist of GitHub download hosts. GitHub's release API must provide the asset's SHA-256 `digest`; the full download must match its digest and size. This trusts the configured GitHub repository/account and HTTPS; it is not independent Authenticode signing or protection against a compromised publisher account.

`update-manifest.json` format 1 records the numeric version, `win-x64` platform, and every package file's relative path and SHA-256. The updater checks package version against the new EXE's file version, required runtime/bridge/license files, archive paths and sizes, duplicates, links and all hashes. The same manifest identifies installer-owned paths in the existing copy. Package files absent from the new version are retained rather than deleted; this avoids deleting user-added material. A rare future reintroduction of an unowned colliding path may require a fresh-folder installation.

The helper is copied from the running app, waits for the exact parent process to exit, rejects changed staging data, preflights destination locks, backs up replacements and writes the new manifest last. A per-folder installation mutex blocks normal app startup during the copy. Helper/restart processes exclude inherited provider-token and private bridge/session environment variables; normal credential loading remains unchanged.

## Checks and limits

`tests/update` uses fake releases/HTTP, fake installations, simulated activity and offscreen controls. It checks version filtering, repository/hash checks, unsafe archives, ownership conflicts, in-use files, failed-copy rollback, update navigation, automatic preferences, normal-close install and manual-close cancellation/restart. The published-helper check uses the real packaged EXE only in its update-helper mode against isolated fake app files.

These checks do not establish a live GitHub-to-installed-version update, physical toy stopping, headset behavior or a clean Windows installation. Those remain separate verification steps after a public release exists.


Public 0.7.1 discovery, complete download, digest/manifest validation and staging have also been verified through the production updater with an isolated fake installation using the original 0.7.0 ownership manifest. This does not install or restart a real app.

Public 0.7.2 discovery, full download and digest/manifest validation are verified using the production updater with a fake 0.7.1 installation. The real installed app and its preferences are unchanged by verification.
