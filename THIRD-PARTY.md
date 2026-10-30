# Third-party components

The [ShockWave Source-Available Contribution License 1.0](LICENSE) applies to the project's own work. It does not replace the licenses of third-party software or grant rights to vendor SDK binaries.

| Component | Where its version and notices are recorded |
| --- | --- |
| .NET / Windows Desktop | Project target: `overlay/ShockWave.Overlay.csproj`. Self-contained packages must include the runtime's license and notices. |
| Node.js | Distribution packaging records the pinned runtime and includes its official LICENSE. |
| SignalR, werift, ws and transitive Node dependencies | `bridge/package.json` and `bridge/package-lock.json`; installed packages retain their original licenses. |
| Vortice / SharpGen and rendering dependencies | Production D3D11 bitmap copy: `overlay/ToyControl/ToyControl.Shared.props`. Historical shader compiler: test-only `tests/legacy-renderer/LegacyRenderer.props`. Notices: `work/ShaderPanelTest/THIRD-PARTY.txt` and `work/ShaderPanelTest/licenses/`. |
| Valve OpenVR | Pinned v2.12.14, commit and SHA-256 records in `overlay/SteamVr/vendor/version.json`; license in `overlay/SteamVr/vendor/LICENSE-OpenVR.txt`. `overlay/prepare-steamvr.ps1` downloads and verifies the pinned Windows DLL; it is not committed to source. SteamVR itself is installed separately. |
| Intiface Engine / Buttplug | `overlay/native/buttplug/LICENSE.txt`, `README.md` and `VERSION.md`. The pinned executable is downloaded and verified by `overlay/prepare-buttplug.ps1`; it is not committed to source. |
| Legacy Lovense BLE DLL | Optional proprietary vendor component, excluded from public source and the current portable package. See `overlay/native/lovense/README.md` and applicable vendor terms. |
| Inno Setup | Optional installer build tool with its own license. See `overlay/installer/README.md`. |

Review the complete dependency tree and retain notices when packaging a release. Do not apply the project's eventual license label to third-party directories as though they were solely ShockWave code.

VRChat, VRCFury, SPS and hardware-provider names describe interoperability. Unity, VRChat SDKs, vendor applications, avatars and third-party Unity projects are not included with this repository.
