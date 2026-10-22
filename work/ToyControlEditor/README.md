# ShockWave Avatar Panel

## Install and update with Creator Companion

1. [Add the ShockWave repository to VCC](https://szeb95.github.io/ShockWave/). Add the [official VRCFury repository](https://vcc.vrcfury.com/) if it is missing.
2. In VCC **Manage Project**, add **ShockWave Avatar Panel**. It depends on the VRChat Avatars SDK and VRCFury; Unity 2022.3 is supported.
3. Open **Tools → ShockWave → Panel** in Unity. Choose the avatar and **Radial menu path** (default `ShockWave`; e.g. `Utilities/ShockWave`; empty places the controls at the menu root).
4. Explicitly click **Add Toy Control to Avatar** or **Update Toy Control on Avatar**. Save and upload when ready.
5. Run **ShockWave 0.7.9 or later**, enable OSC in VRChat, and use **Show panel** in your chosen menu.

VCC updates the Editor tools. Updating generated avatar content and uploading remain explicit actions. The package migrates only the old `Assets/Editor/SzebsToysToyControl` tool folder, preserving `Assets/SzebsToys` content. Do not keep a second manually copied tool folder alongside the package. The Windows ZIP also contains a manual `UnityEditor/SzebsToysToyControl` fallback.

Installation replaces only this tool's direct-child host and creates new assets under `Assets/SzebsToys/ToyControl`. Earlier generated assets remain available; the change can be undone. It preserves existing avatar objects, controllers and menus. It does not save scenes, install itself, switch scenes or upload automatically.

## Placement and menu

The panel is **off by default in Unity preview and VRChat**. The radial menu has exactly:

- **Show panel**
- **Follow right hand** — follows hand movement **and wrist rotation**
- **Panel size** — saved locally, default **20%** on a fresh setup

Normally the panel follows the avatar's movement and rotation. Showing it initially places it beside the right hand. Touch either floating strip and use Fist, Hand Gun or Thumbs Up to reposition; releasing and leaving the strip resumes avatar following. Movement suppresses selection/drawing until release. There is no world-lock menu option. Existing saved size values may override the new default. Size maps 0–100% to 0.4–1.6 times the 74 cm base width; the new 20% default is about 47 cm wide, excluding strips.

## Touch and either-hand pointer

**Touch / Pointer**, beside Play loop, switches modes in the app or avatar. Touch is the startup default. Aim like a finger pistol with either hand, then curl the index into **Thumbs Up** to click or hold to draw. Finger curl does not steer the calibrated wrist direction. The starting hand owns a drag; a held second hand cannot take it over. Hover sends no hardware command. Changing modes, losing the target or losing the panel link cancels an unfinished capture and requires a fresh release/press.

The v6 pointer replaces scene raycasts with two rigid local Contact points per hand. ShockWave reconstructs the panel-relative pose and intersects the ray with the visible panel plane. Both visuals use that same hit: misses, transparent gaps, incoherent samples, excessive distance and grazing angles hide the beam and dot. The dot stays 0.2 mm in front of the board at normal size. Cursor feedback bypasses the larger graph refresh. This needs both the new app and an updated/uploaded avatar; the old native pointer does not provide the new pose fields.

Finger rows have inset, shallower targets with vertical gaps. Only one row can toggle per physical touch; all rows must release for 200 ms before another choice. An ambiguous overlap in one received frame toggles neither.

## Toys and shockers

The top-right button switches **Toys / Shockers**. Local and **Remote {name}** toy functions share the list; generated four-character suffixes are hidden without changing IDs. Select functions, then touch/draw. Without a selected connected function, drawing is ignored. Release with Record off holds the last value. Record stays enabled until toggled off; each fresh touch records and release loops a valid 0.2–60 second capture. Play restarts selected cached patterns. Stop clears output without changing the Record latch. Recordings are RAM-only.

The app's **Shocker control** tab matches the avatar's second page. The vertical sliders set intensity and duration without sending a command. Vibrate and Sound submit once. Shock requires **right ARM → release → left SHOCK within 3 seconds**, freshly for every command. Holding/dragging across the circles never confirms. Target/value/page/mode/link changes cancel confirmation. Existing local/provider/remote limits, pause, cooldown and Test mode apply; an intensity of 80 capped at 40 becomes 40. An already accepted shocker command may finish.

Owner Input/Stop changes replace a function's curve, cancel its unfinished capture/loop and deselect it. Sibling functions stay independent. A fresh touch/Play may take control again. Requested graph percentages are distinct from capped/quantized submission; software feedback is not confirmed physical movement.

## Connection and runtime limits

The app owns one OSC listener (default loopback input 9001, feedback 9000). Another listener on that port is reported. It forwards accepted PiCtrl input to the existing OpenShock bridge; toy control does not need that bridge. No VRChat config or launch arguments are rewritten.

Both heartbeat directions alternate every 250 ms; app freshness is sent before display updates. Two-second visible-panel/source expiry remains unchanged. Hide, avatar change or link loss stops VRC-owned functions; stale touches/presses/renewals cannot restart them. Late app startup recovers after two fresh avatar pulses without replaying held input. Reconnect needs fresh touch/Play. Test mode/reset/disconnect retain existing stopping and no-replay rules.

The default five updates per toy per second is shared by that toy's functions, with a shared adapter ceiling of 20 ordinary commands/sec. Cursor feedback does not increase those hardware limits. Physical response and multi-function latency vary.

## Avatar cost and verification

The v6 panel uses **616 local expression parameters, zero synced parameter cost**, and **13 FX layers**. Only Size is saved. There are **41 self-only, Local Only receivers** and **four Local Only senders**, four parent constraints, no native rays/physical collider, no particles, PhysBones, runtime C# or Udon. Seven renderers have 34 triangles total: the board, two strips, two dots and two eight-vertex/two-bone skinned beams. The beam bounds no longer reserve a 6.1 m cube. Textures and material rendering still contribute to cost; consult the avatar's actual SDK statistics.

Verification uses a separate scratch project, never the real avatar Editor. Production fake-device, geometry and loopback checks cover pointer selection/drag/confirmation, exact surface mapping at multiple sizes, rejected angles/misses, neighbor contacts, limits and no replay. Scratch SDK checks cover pose reconstruction on rotated panels, animated cursor positions, preview visibility, custom menu path, placement/size, shaders and preserved avatar assets. Offscreen images are inspected in Linear/Gamma rendering. These are software checks; uploaded-avatar accuracy, controller gestures, motion latency and physical output still need headset/device testing.

Build the VPM package with PowerShell 7: `./scripts/package-panel.ps1`. It preserves existing GUIDs, generates deterministic remaining metadata, produces a package-root ZIP and checksum, and updates `vpm/index.json`. Publish the package under `panel-v<version>` with GitHub **latest disabled** so it cannot replace the Windows updater release. The Pages workflow serves `vpm/`; see [publication guidance](../../docs/GITHUB-PUBLISHING.md).
