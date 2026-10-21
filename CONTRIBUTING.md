# Contributing to ShockWave

Thanks for helping fix ShockWave. Small, focused bug fixes, regression tests and documentation corrections are easiest to review. Discuss a broad fix or protocol correction in an issue before implementation.

ShockWave uses a [contribution-only source-available license](LICENSE). It permits contribution branches/forks for proposing fixes to official ShockWave. It does not grant permission to publish independent modified releases or reuse the code in unrelated projects. GitHub's platform rights and separately licensed third-party components remain unaffected.

Contributors retain copyright in their own work. The contribution terms allow the maintainer to use, modify and distribute submitted fixes within the official ShockWave project; no copyright assignment is required. Read the license before submitting code.

## Report a problem

Include the ShockWave version, Windows version, affected page, steps to reproduce and what you expected. Identify toy models and the selected backend when relevant, without Bluetooth addresses or private device IDs. Say whether your result is a simulated check, a physical-device test or a headset test.

Remove API keys, invitation codes, hardware identifiers and personal information from screenshots and logs. Never upload the `%LOCALAPPDATA%\SzebsToys` folder. For a security issue, follow [SECURITY.md](SECURITY.md).

## Source layout

- `overlay/`: native .NET 8 Windows Forms app and packaging sources.
- `bridge/`: Node.js ESM OSC, OpenShock, PiShock and direct connection workers.
- `tests/`: isolated native and packaging checks.
- `work/ToyControlEditor/`: optional Unity editor tools and avatar shaders.
- `work/ShaderPanelTest/`, `work/CurvePanelVRDemo/`: source shared by the production renderer and control contract; these directories are needed to build the app.
- `docs/`: user guides, contracts, implementation notes and verification evidence.

## Build and check

Use Windows x64, the .NET 8 SDK, Node.js 24.21.0 and PowerShell 7.

```powershell
Push-Location bridge
try {
    npm ci --ignore-scripts --no-audit --no-fund
    if ($LASTEXITCODE -ne 0) { throw 'Dependency installation failed.' }
}
finally { Pop-Location }

$bridgeTests = @(Get-ChildItem -LiteralPath bridge -Filter '*.test.mjs' | ForEach-Object FullName)
node --test @bridgeTests
if ($LASTEXITCODE -ne 0) { throw 'Bridge checks failed.' }

dotnet build overlay/ShockWave.Overlay.csproj --configuration Release
if ($LASTEXITCODE -ne 0) { throw 'Windows build failed.' }
```

The bridge tests use fixtures and loopback peers. Building does not launch the normal app. The GitHub workflow runs these source checks on a Windows runner; it does not establish physical-device compatibility. Use `overlay/build.ps1` for the developer publish and hidden-worker startup checks.

Run the relevant existing native suite for behavior changes. Some UI checks require an interactive Windows desktop; direct connection checks can use public signalling helpers. Read that suite's source/guide before running it. Keep real credentials and hardware out of automated tests.

## Preserve behavior and user data

- Preserve settings migration, Windows-encrypted token storage, Test mode and individual device limits.
- Keep shocker output one-shot: no command retry, queue replay or replay on reconnect.
- Keep Stop, source freshness, permission and ownership rules intact.
- Never automatically save or change an existing Unity scene, avatar, prefab, package, project settings or editor layout. Verify Unity tools in a separate scratch project. Avatar installation remains an explicit user action.
- Before integration work, read `AGENTS.md` and the matching architecture/protocol/status documents under `docs/lovense-remote/`. Document shared contract changes and actual verification results.
- Do not commit generated builds, vendor binaries, backups, logs, credentials or private identifiers.

## Pull requests

Explain the user-visible problem and resulting behavior. Include relevant checks and their limits. Link an issue when one exists. State explicitly if physical-device/headset testing was not performed; simulated checks are useful, but establish different evidence.
