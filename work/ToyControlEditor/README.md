# ShockWave Avatar Panel

## Install and update with Creator Companion

1. [Add the ShockWave repository to VCC](https://szeb95.github.io/ShockWave/). Add the [official VRCFury repository](https://vcc.vrcfury.com/) if it is missing.
2. In VCC **Manage Project**, add **ShockWave Avatar Panel**. It depends on the VRChat Avatars SDK and VRCFury; Unity 2022.3 is supported.
3. Open **Tools → ShockWave → Panel** in Unity. Choose the avatar and **Radial menu path** (default `ShockWave`; e.g. `Utilities/ShockWave`; empty places the controls at the menu root).
4. Explicitly click **Add Toy Control to Avatar** or **Update Toy Control on Avatar**. Save and upload when ready.
5. Run **ShockWave 0.7.10 or later**, enable OSC in VRChat, and use **Show panel** in your chosen menu.

VCC updates the Editor tools. Updating generated avatar content and uploading remain explicit actions. The package migrates only the old `Assets/Editor/SzebsToysToyControl` tool folder, preserving `Assets/SzebsToys` content. Do not keep a second manually copied tool folder alongside the package. The Windows ZIP also contains a manual `UnityEditor/SzebsToysToyControl` fallback.

Installation replaces only this tool's direct-child host and creates new assets under `Assets/SzebsToys/ToyControl`. Earlier generated assets remain available; the change can be undone. It preserves existing avatar objects, controllers and menus. It does not save scenes, install itself, switch scenes or upload automatically.

## Placement and menu

The panel is **off by default in Unity preview and VRChat**. The radial menu contains:

- **Show panel**
- **In right hand**
- **Back of right hand**
- **Panel size** — saved locally, fresh default **20%**

The two hand toggles use one local mode value and are mutually exclusive. **Both off locks the panel in the world.** Looking around, walking, stick movement and stick turns leave it where you placed it. Hide/show brings it back beside the right hand. This is world locking; it does not use the camera's playspace-following behavior.

Both hand modes follow wrist position and rotation. Automatic alignment uses the extended index direction and index/little-finger side, avoiding arbitrary hand-bone axes. In hand holds the board beside the hand; Back of hand puts it above the back surface. Exact offsets can vary by avatar and controller grip.

### Capture your adjusted positions

1. Open **Tools → ShockWave → Panel** and choose the avatar. In paused Play mode, choose the live avatar instance if necessary.
2. Adjust the panel to your preferred in-hand pose, then click **Capture In hand**.
3. Adjust it to the back-of-hand pose and click **Capture Back of hand**.
4. Exit Play mode, reselect the scene avatar if needed, and explicitly **Update Toy Control on Avatar**. Save/upload when ready.

Capture reads the actual visible Panel transform relative to the right wrist; it does not move objects or save the scene. Whole-host Transform values in screenshots cannot define a portable hand offset without that hand's pose. The captured values are saved as `Placement.json` in the new generated content folder during Update and retained on later updates. **Load saved placements from avatar** restores them in the window; **Use automatic placements** clears them for the next explicit Update.

### Handles and fitting the panel in VR

A **Fist** on either handle grabs a floating panel in **Touch or Pointer** mode. Hold with either hand and draw with the free hand. The gripping hand's pointer and click are suppressed; pistol aiming and Thumbs Up do not grab. The short attach/drop guards require fresh input after repositioning.

To adjust a right-hand attachment, grab a handle with the **left hand**, move/rotate the panel and release. It returns to the right wrist using the new offset. The mounted right wrist ignores its own grip contacts.

To create a hand fit on the fly:

1. Grab a floating panel with a fist.
2. While holding, select **In right hand** or **Back of right hand** in the radial menu. It keeps following the grip while held.
3. Position it how you want relative to the right hand, then release the fist. It attaches there without jumping to the preset.

The adjusted fit survives hide/show and toggling its hand mode off/on. **A new handle grab replaces the previous temporary fit**; avatar reload resets it too. In-game adjustments do not edit the Unity avatar or persist to disk. Use the Editor capture procedure above for permanent uploaded defaults. With both hand modes off, releasing the handle returns to world lock after the touch guard.

Existing saved Size can override the new default. Size maps 0–100% to 0.4–1.6 times the 74 cm base width; 20% is about 47 cm wide, excluding strips.

## Touch and either-hand pointer

**Touch / Pointer**, beside Play loop, switches modes in the app or avatar. Touch is the startup default. Aim like a finger pistol with either hand, then curl the index into **Thumbs Up** to click or hold to draw. Finger curl does not steer the calibrated wrist direction. The starting hand owns a drag; a held second hand cannot take it over. Hover sends no hardware command. Changing modes, losing the target or losing the panel link cancels an unfinished capture and requires a fresh release/press.

A small guide at the bottom of both avatar pages reads: **Fist = grab handle | Finger gun = point | Curl index (thumbs up) = click**.

The v7 pointer replaces scene raycasts with two rigid local Contact points per hand. ShockWave reconstructs the panel-relative pose and intersects the ray with the visible panel plane. Both visuals use that same hit: misses, transparent gaps, incoherent samples, excessive distance and grazing angles hide the beam and dot. The dot stays 0.2 mm in front of the board at normal size. Cursor feedback bypasses the larger graph refresh. This needs both the new app and an updated/uploaded avatar; the old native pointer does not provide the new pose fields.

Unknown battery readings and readings on disconnected toys are hidden in desktop/avatar rows. Known connected readings remain visible.

Finger rows have inset, shallower targets with vertical gaps. Only one row can toggle per physical touch; all rows must release for 200 ms before another choice. An ambiguous overlap in one received frame toggles neither.

## Toys and shockers

The top-right button switches **Toys / Shockers**. Local and **Remote {name}** toy functions share the list; generated four-character suffixes are hidden without changing IDs. Select functions, then touch/draw. Without a selected connected function, drawing is ignored. Release with Record off holds the last value. Record stays enabled until toggled off; each fresh touch records and release loops a valid 0.2–60 second capture. Play restarts selected cached patterns. Stop clears output without changing the Record latch. Recordings are RAM-only.

The app's **Shocker control** tab matches the avatar's second page. The vertical sliders set intensity and duration without sending a command. Vibrate and Sound submit once. Shock requires **right ARM → release → left SHOCK within 3 seconds**, freshly for every command. Holding/dragging across the circles never confirms. Target/value/page/mode/link changes cancel confirmation. The cursor and navigation stay active while page acknowledgement arrives; commands stay blocked. The avatar pulses its shocker-view acknowledgement every 250 ms, allowing two fresh alternating pulses to recover a lost initial Ready packet. Recovery expires after 750 ms without a pulse and never replays a held press. Existing local/provider/remote limits, pause, cooldown and Test mode apply; an intensity of 80 capped at 40 becomes 40. An already accepted shocker command may finish.

Owner Input/Stop changes replace a function's curve, cancel its unfinished capture/loop and deselect it. Sibling functions stay independent. A fresh touch/Play may take control again. Requested graph percentages are distinct from capped/quantized submission; software feedback is not confirmed physical movement.

## Connection and runtime limits

The app owns one OSC listener (default loopback input 9001, feedback 9000). Another listener on that port is reported. It forwards accepted PiCtrl input to the existing OpenShock bridge; toy control does not need that bridge. No VRChat config or launch arguments are rewritten.

Both heartbeat directions alternate every 250 ms; app freshness is sent before display updates. Two-second visible-panel/source expiry remains unchanged. Hide, avatar change or link loss stops VRC-owned functions; stale touches/presses/renewals cannot restart them. Late app startup recovers after two fresh avatar pulses without replaying held input. Reconnect needs fresh touch/Play. Test mode/reset/disconnect retain existing stopping and no-replay rules.

The default five updates per toy per second is shared by that toy's functions, with a shared adapter ceiling of 20 ordinary commands/sec. Cursor feedback does not increase those hardware limits. Physical response and multi-function latency vary.

## Avatar cost and verification

The v7 panel uses **618 local expression parameters, zero synced parameter cost**, and **13 FX layers**. Only Size is saved. There are **41 self-only, Local Only receivers** and **four Local Only senders**, four parent constraints, no native rays/physical collider, no particles, PhysBones, runtime C# or Udon. Seven renderers have 34 triangles total: the board, two strips, two dots and two eight-vertex/two-bone skinned beams. The beam bounds no longer reserve a 6.1 m cube. Textures and material rendering still contribute to cost; consult the avatar's actual SDK statistics.

Verification uses a separate scratch project, never the real avatar Editor. Production fake-device, geometry and loopback checks cover pointer selection/drag/confirmation, exact surface mapping at multiple sizes, rejected angles/misses, neighbor contacts, limits and no replay. Scratch SDK checks cover pose reconstruction on rotated panels, animated cursor positions, preview visibility, custom menu path, placement/size, shaders and preserved avatar assets. Offscreen images are inspected in Linear/Gamma rendering. These are software checks; uploaded-avatar accuracy, controller gestures, motion latency and physical output still need headset/device testing.

Build the VPM package with PowerShell 7: `./scripts/package-panel.ps1`. It preserves existing GUIDs, generates deterministic remaining metadata, produces a package-root ZIP and checksum, and updates `vpm/index.json`. Publish the package under `panel-v<version>` with GitHub **latest disabled** so it cannot replace the Windows updater release. The Pages workflow serves `vpm/`; see [publication guidance](../../docs/GITHUB-PUBLISHING.md).
