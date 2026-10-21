# Lovense and remote-control framework

**Shocker control, 2026-10-21:** added the desktop Shocker control tab and matching avatar Shockers page. Single-select local/remote list, vertical intensity/duration sliders, Vibrate/Sound and right-arm → release → left-confirm within three seconds. Sliders send nothing; local manual and remote receiver limits retain their distinct gates. Toys now share one list with Remote names and no generated four-character suffixes. VR shocker control requires the updated avatar page acknowledgement. See PROTOCOL v5, [connection guide](../REMOTE.md) and STATUS for checks/release. Explicit avatar Update/upload and physical verification remain user actions.

**Direct app connections, 2026-10-19:** ShockWave now has a Connect tab between SPS and Log, using private-code pairing, public PeerJS setup signalling and Google STUN for direct app-to-app control. Both Connect states authorize supported connected functions without extra per-toy approvals, while the receiver enforces its existing limits, Test mode, ownership, expiry and Stop. Remote functions appear in Toy control; remote shocker inventory/transport prepares the later tab. TURN is disabled and no hosting account/server setup is required. Native/public-helper tests cover four simulated toys in both directions, disconnect and zero-activity reconnection on one Windows PC. Separate internet networks and physical response remain pending. See [connection guide](../REMOTE.md), the implemented direct v1 section of PROTOCOL, and STATUS for the separate portable build. Earlier remote-pending text below records the previous planning baseline.

**Current local integration, 2026-10-10:** the confirmed avatar shader is in main ShockWave as **Toy control**, below Toy panel. Actual inventory supplies separate function rows; desktop/VR controls share RAM recordings, existing Sensitivity/Max %, Test mode and scheduling. Desktop Input/Stop replaces only that function's VR loop/value. The **SPS** tab recognizes generated names/addresses automatically and routes fresh signals to connected assigned toy functions without separate SPS Start/Stop. Preview, mapping controls and existing owner Stop remain; see [SPS.md](SPS.md) for freshness/capability limits. PiCtrl retains private Node IPC. The finished Editor tool adds **VRC toy control** on explicit user installation. See PROTOCOL.md v2 and STATUS.md. The SPS body map now includes approach-calibrated socket depth, hover labels and read-only OSCQuery contact refresh. Physical SPS verification and remote transport remain pending; earlier standalone-only text describes historical versions.

Planning baseline: 2026-10-07. Implementation status: [STATUS.md](STATUS.md).

## Intended result

Two people run ShockWave. Each app can discover supported local Lovense toys, show their functions, and connect to them directly. A person can invite another client, choose which toy functions to share, and send or receive manual controls, patterns, or mapped OSC values.

Users should not need standalone Lovense Remote or Intiface. The initial recurring hosting budget is EUR 0. Remote control must report connection problems clearly and stop local continuous output when its authorized live input expires.

## Current foundation

The project already contains:

- A .NET 8 WinForms app in `overlay/`.
- A Node.js ESM bridge in `bridge/`.
- JSON-line stdin commands for configuration and manual OpenShock control.
- OSC input and avatar feedback, OpenShock live events, test mode, individual limits, and saved settings.

It now provides local inventory, automatic connection, independent function controls, shared output scheduling, simulated discovery/output, and an optional native Lovense BLE adapter; see [setup and verification](LOCAL-TOYS.md). The owner requested ready connected controls without Seconds/Run, then clarified that each function still needs a separate Input slider: 0 off, 100 full request within Sensitivity/Max %. The controller holds this local value until replaced/stopped/disconnected. Local Toy control and the automatic SPS body map are implemented, including contact refresh and approach calibration; physical SPS accuracy/output and remote transport remain pending. The user reported physical connection, motor control and battery reading, with latency/reconnect failures under follow-up. Native BLE runs in a hidden app process so recovery can replace native process state without reopening the interface. Physical Stop/reconnect/rates remain unverified; the vendor DLL is locally supplied. Local control needs no OpenShock token. Test mode is shared; configured OpenShock commands retain their own credentials and existing behavior. See STATUS.md for current EXE builds.

Read [ARCHITECTURE.md](ARCHITECTURE.md), [PROTOCOL.md](PROTOCOL.md), and [SOURCES.md](SOURCES.md) before implementing.

## Implementation chats

| Chat | Brief | Main result |
| --- | --- | --- |
| 01 local toys detection | [01 brief](chats/01-local-toys-detection.md) | Discover and identify toys and supported functions, without activating them. |
| 02 lovense into programm integration | [02 brief](chats/02-lovense-integration.md) | Native local control and usable toy cards/settings. |
| 03 szebs relay server | [03 brief](chats/03-szebs-relay-server.md) | Free connection setup / relay provider integration and quota handling. A rented server is not required for the first version. |
| 04 connecting clients | [04 brief](chats/04-connecting-clients.md) | Pairing, authenticated sessions, WebRTC channels, and bidirectional control. |
| 05 permissions, limits, and stopping | [05 brief](chats/05-permissions-limits-stopping.md) | Receiver-controlled permissions, local stop, expiry, and reconnect rules. |
| 06 OSC and SPS mappings | [06 brief](chats/06-osc-sps-mappings.md) | Continuous mappings and avatar-zone routing compatible with the intended inputs. |
| 07 testing and packaging | [07 brief](chats/07-testing-packaging.md) | Network fault checks, physical-device evidence, and a distributable Windows app. |
| 08 VR pattern controller | [08 brief](chats/08-vr-pattern-controller.md) | Confirmed HLSL design integrated into main Toy control with local leases, OSC feedback and a finished Editor installer; real VR-to-BLE checks pending. |

05 is a cross-cutting requirement: its core rules must be built into 02 and 04 before real output is enabled. 07 also starts early with mocks; it is not just a final packaging task.

## Recommended order

1. Confirm the shared device model and draft protocol during 01.
2. Implement 01 discovery and basic local connection status.
3. Implement 02 local control together with the applicable 05 rules.
4. In 03, confirm the free account allowance, quota behavior, and a WebRTC implementation suitable for the actual Windows/Node stack.
5. Implement 04 using simulated devices first, then real devices with 05 enforced.
6. Add 06 mappings after manual local and remote controls work.
7. Complete 07 packaging, regression checks, and the release checklist.

03 can be investigated independently of local hardware. Do not enable real remote output before 05 is verified.

## Initial scope

- Windows x64, matching the existing application.
- One-to-one sessions; both participants may send and receive when separately authorized.
- Lovense vibration as the first remote physical capability, with a model that can later describe multiple motors and other functions.
- 30-60 network state frames per second as a starting target, independent of the toy's measured command rate.
- Direct WebRTC data channels, with TURN fallback where needed.
- Free service integration with replaceable provider configuration and visible quota failures.
- Existing OpenShock behavior remains supported. Its discrete actions are not converted into a continuous high-frequency stream.

Mobile ports, group sessions, voice/video, additional brands, and exact replication of every OSCGoesBrrr feature are later work unless a future chat explicitly expands scope.

## How to start a new chat

Copy the starter prompt from its brief. Each chat should read these files rather than depend on remembering the conversation.

At the end of a chat, update [STATUS.md](STATUS.md), note changed contracts, and record what was actually verified. Do not describe an implementation as physically tested when only a mock or build check passed.
