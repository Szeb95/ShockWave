<p align="center">
  <img src="docs/images/shockwave-banner.svg" alt="ShockWave — desktop and VRChat toy controls" width="100%">
</p>

<p align="center">
  <strong>Desktop and VRChat controls, together in one Windows app.</strong><br>
  Bluetooth toys · OpenShock · PiShock · OSC / SPS · Direct app connections
</p>

<p align="center">
  <a href="https://github.com/Szeb95/ShockWave/releases">Downloads</a> ·
  <a href="#getting-started">Getting started</a> ·
  <a href="#guides">Guides</a> ·
  <a href="https://github.com/Szeb95/ShockWave/issues">Report an issue</a> ·
  <a href="CONTRIBUTING.md">Contribute</a>
</p>

# ShockWave

ShockWave is a native Windows application for supported Bluetooth toys and OpenShock or PiShock devices. Use desktop controls, draw and loop patterns, connect VRChat avatar inputs, or pair with another person running ShockWave.

**Development preview.** Get the complete Windows package from [Releases](https://github.com/Szeb95/ShockWave/releases/latest). See [verification and known limits](#verification-and-known-limits) before using hardware.

## What you can do

| Feature | How it works |
| --- | --- |
| **Bluetooth toy controls** | Discover supported Lovense toys, see individual functions and battery readings, and adjust Input, Sensitivity and Max %. |
| **Draw and play patterns** | Select functions, draw curves, record and loop them through Toy control. Touch and Pointer modes also work with the optional avatar panel. |
| **Shocker control** | Use OpenShock or PiShock with individual limits, pause and cooldown. Shock uses a fresh ARM → release → confirm action for every command. |
| **VRChat and SPS** | Shared automatic SPS controls, plug/socket discovery and optional manual body-map connections. |
| **Direct app connections** | Pair two ShockWave apps using a private code. Each receiver controls its own hardware and applies its own limits. |
| **Test mode** | Preview supported control paths without sending physical output. |

## A look inside

![ShockWave Toy control preview](docs/images/toy-control.png)

<details>
<summary>Shocker control, Connect and Update previews</summary>

![ShockWave Shocker control preview](docs/images/shocker-control.png)

![ShockWave Connect preview](docs/images/connect.png)

![ShockWave Update preview](docs/images/update.png)

</details>

These are software-test previews with simulated devices and empty connection codes. They illustrate the interface; they are not evidence of physical-device testing.

## Getting started

**Requirements:** Windows 10 version 1809 or newer, or Windows 11, on an x64 PC. Bluetooth control requires a compatible Windows Bluetooth adapter and supported toy. OpenShock and PiShock require their own hardware and account credentials. VRChat features require VRChat with OSC enabled.

To install:

1. Open [Releases](https://github.com/Szeb95/ShockWave/releases) and read that version's notes.
2. Download its Windows x64 package. For a portable ZIP, extract the **whole** archive and keep its folders together, then run `ShockWave.exe`.
3. Open **Settings** and enable **Test mode** before trying the controls. Fresh settings currently start with Test mode off.
4. Follow the guide for [Bluetooth toys](docs/lovense-remote/LOCAL-TOYS.md), [OpenShock](overlay/README.md) or [PiShock](docs/PISHOCK.md).
5. For another ShockWave user, follow [Connect setup](docs/REMOTE.md). Keep connection codes private; connecting authorizes the paired person's supported controls within your limits.

The current Bluetooth backend bundles Intiface Engine as a hidden child process; no separate Intiface or Lovense Remote application is required. The optional legacy Lovense DLL has separate vendor terms and is excluded from the public source and current portable package.

Avatar tools are optional. Import them into your project and explicitly add/update the panel yourself using the [Toy Control guide](work/ToyControlEditor/README.md). The Windows app does not install avatar content or save Unity scenes.

### Updates and saved settings

**Update**, directly below Settings, shows the installed version, checks for published releases and links to the new version on GitHub. A newer compatible release adds **Update available** beside the Toy panel heading; clicking it opens Update. The **Update** button downloads and verifies the package, uses the existing normal-close confirmation, installs it and restarts ShockWave.

**Automatic update** defaults off. When enabled, new releases download in the background and install on your next normal app close, without reopening the app. Cancelling a close warning keeps ShockWave running. Install the first complete updater-enabled package manually; subsequent newer stable releases can be offered in the app. See [update setup and limits](docs/UPDATES.md).

Settings, device limits, mappings and encrypted credentials use the existing `%LOCALAPPDATA%\SzebsToys` data folder, independently of the program folder. Keep this folder private. See [the publication guide](docs/GITHUB-PUBLISHING.md) for packaging and release steps.

## Guides

| Topic | Guide |
| --- | --- |
| Windows interface, OpenShock and settings | [Windows app](overlay/README.md) |
| PiShock accounts and devices | [PiShock setup](docs/PISHOCK.md) |
| Bluetooth setup, compatibility and limits | [Local toys](docs/lovense-remote/LOCAL-TOYS.md) |
| Drawing, Touch / Pointer and avatar installation | [Toy Control](work/ToyControlEditor/README.md) |
| SPS discovery and body mappings | [SPS](docs/lovense-remote/SPS.md) |
| Pairing two apps and connection limitations | [Connect](docs/REMOTE.md) |
| Standalone OSC bridge | [Bridge](bridge/README.md) |
| Current implementation and test evidence | [Workstream status](docs/lovense-remote/STATUS.md) |

## Verification and known limits

- Automated checks cover simulated devices, control limits, fresh-input rules, disconnect behavior and interface rendering. Physical Bluetooth response, stop timing and headset behavior still need model-specific verification.
- Direct app pairing has been exercised with two clients on one PC. Separate household/mobile networks still need testing. There is **no TURN relay fallback**; some networks cannot establish a direct connection.
- Stop cancels pending confirmation and continuous toy activity. A shocker command already accepted by its provider can continue for its bounded duration.
- Current Windows packages are unsigned. A clean-PC installation test remains pending.
- Use the relevant release notes and [detailed status](docs/lovense-remote/STATUS.md) to distinguish implemented features, simulated checks and physical tests.

## Build from source

Install the **.NET 8 SDK**, **Node.js 24.21.0** and **PowerShell 7** on Windows x64. From the project root:

```powershell
./overlay/build.ps1
```

The build restores locked Node dependencies when needed, verifies/downloads the pinned Bluetooth engine, publishes the app and checks its hidden worker without a physical device. The default output is `overlay/dist/shockwave/ShockWave.exe`.

This developer build requires the .NET 8 Desktop Runtime on the machine that runs it. Use `overlay/package-release.ps1` for a complete self-contained Windows ZIP with the updater ownership manifest. The [older installer pipeline](overlay/installer/README.md) still needs alignment with the current Bluetooth and remote package; it is not the update payload.

## Contributing

Bug reports, documentation improvements and focused pull requests are welcome. Start with [CONTRIBUTING.md](CONTRIBUTING.md), and use [SECURITY.md](SECURITY.md) for sensitive reports. Never upload credentials, invitation codes, personal configuration or private device identifiers.

## License and acknowledgements

ShockWave uses the [ShockWave Source-Available Contribution License 1.0](LICENSE). It permits inspecting/building ShockWave and making changes to contribute fixes back to the official project. Publishing independent modified releases or reusing its code in unrelated projects requires separate permission. GitHub's own public-repository viewing/forking permissions and third-party licenses remain applicable.

ShockWave is **source available** under these contribution-only terms. Read [the full license](LICENSE) before using the source or contributing changes.

Third-party components retain their own licenses. See [THIRD-PARTY.md](THIRD-PARTY.md), [rendering notices](work/ShaderPanelTest/THIRD-PARTY.txt) and [Bluetooth engine notices](overlay/native/buttplug/VERSION.md). ShockWave is an independent project; product names identify integrations and do not imply endorsement.
