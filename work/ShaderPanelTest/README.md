# ShockWave Shader Lab

Separate Windows HLSL/Direct3D 11 preview with an optional first-avatar VR OSC demo. Mouse/OSC state is simulated; no Bluetooth, Lovense DLL, OpenShock credentials or remote session is used. Main ShockWave integration waits for the user's VRChat demo confirmation.

## First VR OSC demo

Click **Start VR OSC** to receive local avatar input and send numeric shader feedback; keep the test program running behind VRChat. This is opt-in and uses 127.0.0.1:9001 inbound and 9000 outbound. The existing inventory receiver remains on 9002. A port conflict is reported without taking over another app. `--vr-demo` starts the link automatically; `--vrc-receive` and `--vrc-send` select custom ports.

The new Unity tool **Tools → ShockWave → Curve Panel → VR OSC Demo** generates a separate shader panel, contacts, FX, menu and local parameters attached through VRCFury Full Controller. Its menu is **VR Curve Demo**. Show the panel with OSC enabled, then use **Demo OSC** on the board/menu. All row selection, recording, playback and control actions are handled by this program. Feedback includes names, colors, levels, held lines, independent loop waves and the touch dot. The VR board omits the desktop footer. See `VR-DEMO.md` in the package, or [the source guide](../CurvePanelVRDemo/README.md), for installation, menu touch emulation, axis calibration, transport limits and test steps.

The program and avatar exchange separate heartbeats. Hidden panel, avatar change or missing avatar heartbeat stops simulated activity and requires fresh input. Disabling VR OSC also stops this session. Keep the app running for RAM recordings. Real uploaded-avatar finger/OSC/headset behavior still needs the user's test before main ShockWave integration.

## Open the test

Open `dist/ShockWaveShaderLab.exe`. Click **Demo OSC** to populate eight synthetic toys with ten function rows through the real loopback OSC listener. The demo list is labelled preview only and is not a discovery of your physical toys. Each toy function has its own row: Nora has Vibration and Rotation; Edge has Vibration 1 and Vibration 2. Select any combination of functions, then hold left click in the graph and drag vertically. With Record off, the graph shows eight seconds of history and retains the input level after release. Each deselected function retains its own last requested percentage, independently of sibling functions. Selecting a function does not immediately copy the graph level. **Zero selected** zeros just the selected functions and stops their loops; **Stop all** (or Escape) stops every loop and zeros all preview levels. **Clear curve** clears the visual history without changing function levels or recordings.

## Record and loop in memory

1. Select the functions to record, then click **Record**. It remains enabled until toggled off, recording each fresh touch; time spent waiting is not part of a recording. Stop/Hold/Play or cancelled capture do not change this toggle or automatically restart output.
2. Hold left click inside the graph and draw. Vertical movement sets intensity; time advances automatically. The chosen functions are fixed when the recording starts.
3. Release to finish and immediately loop that pattern on those functions. Deselecting a looping function lets it keep playing independently, while another function can be drawn or recorded.

**Hold selected** pauses selected loops at their current requested level. **Play loop** restarts the selected functions' most recent recordings from the beginning. A fresh manual graph touch overrides selected loops; release with Record off then holds the last drawn value. Stop and Zero keep cached recordings in RAM, but require an explicit Play to restart them. Turning Record off while capturing, leaving the graph, losing mouse capture or losing focus cancels the unfinished recording and holds the last level without starting that recording. A cancelled/too-short recording does not replace a previous completed recording.

Each function keeps one completed pattern, sampled at 30 Hz and linearly interpolated. Valid duration is **0.2–60 seconds**; a shorter tap only holds its level. At 60 seconds, capture ends and looping starts automatically; release before a fresh touch can override it. Loops repeat exactly, so different start/end levels produce a jump at the boundary. Functions recorded together share a pattern; later recordings can give them independent patterns and timing. There is no file save/load, preset library, speed adjustment, overdubbing or horizontal timeline editing yet.

The loop view shows the full recorded duration and a moving playhead. If several independent selected loops are running, the first selected loop supplies this preview; the other rows still show their own current percentages. **Deselected running loops draw their recorded waves in matching colors**, including off-page functions and loops whose current level happens to be zero. They repeat/scroll at their own timing across the same graph time window. With the live timeline that window is eight seconds ending at NOW; with a selected full-pattern preview, background waves align to its duration and current-time playhead. A colored point shows each loop's current value. Repetition extends across the whole visible window as a pattern preview, not a log of past physical output. Non-looping held values keep straight lines. Recording/playback and memory belong to the Windows program, while HLSL draws supplied state, a resampled 240-point primary pattern texture and a bounded 240×256 curve atlas. The eight-second GPU history does not limit recording duration.

Playback keeps advancing while the test app is behind other windows or minimized. After a delayed UI tick, it evaluates the current loop phase rather than replaying missed samples. The program must remain running; closing it clears all recordings. Disconnect/removal discards affected function recordings, and reconnect does not resume them. This is test memory only, with no toy or remote output. Main ShockWave integration remains deferred; the separate VR demo can use this memory through OSC.

All inventory rows remain available regardless of selection; use the arrows or mouse wheel for more than six function rows. The explicit test capacity is 128 devices, 16 functions per device, and 256 total function rows; larger snapshots are rejected without replacing the existing list. Disconnected functions remain visible when their toy is included by the sender and cannot be selected. A full snapshot omits removed devices. Disconnect/reconnect clears all that toy's function values/selections, unless the sender explicitly supplies new levels. The panel keeps rounded cards/buttons and uses ShockWave' charcoal palette with green `#10a37f` accents.

Each function has a stable color, keyed by its toy/function aliases. Renaming, sorting and paging do not change it. A connected, deselected function with a nonzero held level leaves a dim matching horizontal line behind the live curve, even when its row is on another page. A running recorded loop shows its waveform instead of a horizontal line. Hold switches it back to a straight held-value line; Zero/Stop/disconnection/removal removes its activity visual. These are **requested preview values**, not physical motor telemetry. One selected function colors the live curve/dot; multiple selected functions use a white shared curve/dot while keeping their own row colors.

Hold left click to see a larger dot/ring at the actual mouse touch position. Sideways motion moves the dot; vertical motion changes intensity. Time keeps scrolling automatically, and horizontal movement does not edit earlier samples. Release hides the dot while preserving requested levels. `TouchVisuals.hlsli` draws the held lines and dot using supplied scalar/color/XY values, with no particles or scripts inside the shader.

Own / Remote / Both is a visual destination selection only. When VR OSC is enabled, numeric display feedback leaves this viewer for the local avatar. No actuator or remote commands are sent. The inventory viewer listens on `127.0.0.1:9002` by default, independently of VRChat's 9000/9001. To change it, launch `ShockWaveShaderLab.exe --port 9012`. It never changes another program's OSC port or takes over an occupied socket.

## Toy functions over OSC (test contract v2, legacy v1 accepted)

The Windows listener accepts OSC strings, so it can take arbitrary toy names directly. VRChat avatar parameters do not accept these strings. The optional VR demo encodes bounded ASCII names into numeric glyph pairs for the avatar shader. Names and battery are presentation data; IDs below are opaque **test aliases**, never raw Bluetooth IDs or authorization.

| Address | Arguments | Meaning |
| --- | --- | --- |
| `/szebs/shader/inventory` | one OSC string containing a JSON array | Atomic complete inventory snapshot |
| `/szebs/shader/toy` | string id, string name, bool or int connected, int battery, optional float level | Update/create toy metadata; existing functions are retained. Level is accepted only for a toy with one function. Connection int must be 0 or 1 |
| `/szebs/shader/remove` | string id | Remove one row |

Snapshot example:

```json
[
  {"id":"test-a","name":"Lush family","connected":true,"battery":82,
   "functions":[{"id":"v","name":"Vibration"}]},
  {"id":"test-b","name":"My Nora ✨","connected":true,"battery":67,
   "functions":[{"id":"v","name":"Vibration","level":0.6},
                {"id":"r","name":"Rotation","level":0.3}]}
]
```

`connected` defaults true; `battery` defaults -1 (unknown). An explicit `functions` list has 1–16 entries, each with an `id`, `name`, and optional finite 0–1 `level`. A row's identity is the pair **toy ID + function ID**; function IDs must be unique within their toy, but can repeat on another toy. Functions are listed in sender order. Renaming/reordering preserves values, selection and recordings/loop timing by that pair. Removing a function removes its state; readding it starts unselected at zero unless a new explicit level is supplied. **Omitting function levels preserves existing held values and loops**, so repeated metadata snapshots do not erase drawing or restart playback. An explicit incoming level overrides that function's loop and cancels an unfinished recording targeting it; its last completed clip remains available for explicit Play. New functions default unselected at zero.

For legacy v1 senders, omitting `functions` preserves an existing toy's function list, or creates one generic **Input** preview function for a new toy. An optional toy-level `level` is accepted only when this resolves to exactly one function; ambiguous multi-function levels are rejected before mutation. Do not combine toy-level `level` with an explicit `functions` list. Explicit function lists replace the complete function inventory of that toy. Missing devices in a full snapshot are removed. Names/aliases have a 96-character limit, and long visible labels are ellipsized. OSC int/float/bool/string messages and immediate bundles are supported; malformed, future-scheduled, duplicate, oversized and out-of-range data is rejected atomically.

Names do not infer capabilities. These fractions are preview state only; real rotation direction, position/depth and other function semantics still need typed adapter/routing support. This local test interface has no authentication/grants/hardware routing and must not be wired directly to actuator output.

With Python installed, run `python demo-osc.py --watch`. Use `--name "Whatever name"` or `--count 20` to try changing names and paging. Demo inventories replace the current test list. The Demo OSC button sends a real UDP snapshot to the viewer itself; it is not a separate UI-only fixture.

## Edit the shader

`dist/Shaders/Panel.hlsl` contains the panel/background/buttons/graph and the GPU history update. `GraphCore.hlsli` contains reusable graph drawing math; `TouchVisuals.hlsli` contains stateless held-line/touch-dot drawing. Both are independent of Windows/Unity bindings. Edit these files in a text editor; saved changes reload automatically after a short debounce. **Reload** or F5 recompiles immediately. **Open HLSL** loads another compatible panel shader. The host requires `VSMain`, `PSMain` and `HistoryPS`, and the current constant-buffer/texture interface (touch XY, live/row colors, bounded all-function state including per-function `loopView`, `PatternData`, pattern texture at `t2`, and single-channel curve atlas `LoopPatterns` at `t3`). Older custom HLSL files need these bindings updated to show the new visuals. A `.shader` ShaderLab file cannot be loaded directly. Includes must stay inside the selected shader folder. Invalid edits retain the last working shader; click Reload for the full error message.

History is rendered into two 240×1 float textures at 30 samples/sec, alternating read/write textures. The host resamples completed clips into a 240×256 single-channel float atlas (240 KiB); it updates only when clip bindings change, while small per-function phase/window values animate background waves. HLSL draws the lines, fill, grid, markers, row backgrounds and buttons, keeping loop end/start discontinuities separate rather than drawing a false diagonal between them. The Windows host rasterizes transparent text glyphs from OSC names into a texture which HLSL composites. There are no particles. This Windows text/curve upload is distinct from the separate avatar demo's bounded numeric feedback transport. The full avatar wrapper has isolated Unity verification; uploaded-avatar contact/headset/OSC verification remains pending.

Build uses .NET 8 Windows Desktop and pinned MIT-licensed Vortice Direct3D11/D3DCompiler bindings (see `packages.lock.json`). The published test uses the already installed .NET 8 Windows Desktop runtime. It tries a hardware Direct3D 11 device, with software WARP fallback. No separate Unity installation is needed to open the EXE.

## PC avatar rendering evidence

`dist/Shaders/AvatarVisualProof.shader` is a small Unity/PC shader wrapper around the **same** graph/held-line/touch-marker HLSL. It has ordinary animatable scalar/color properties and Unity stereo/instancing macros; it contains no runtime C# component. It demonstrates just those stateless graph visuals, not the full panel, history, recorded-loop atlas, dynamic name feedback, Contacts or OSC routing. The source verifier and usage notes are in `UnityCheck/`.

An isolated Unity 2022.3.22f1 scratch project compiled/rendered the shared HLSL on Direct3D11, checked the 50% colored line, XY movement, release, zero and a stereo-instancing variant. This supports PC-avatar rendering compatibility for these primitives. It is **not** a VRChat upload or a headset/finger tracking test. The later full VR demo supplies horizontal/vertical Contacts, material bindings and numeric history/name feedback; uploaded-avatar calibration remains pending. The original avatar project/editor was never opened or modified by this verification.

## Verification

`dotnet build ShaderPanelTest.csproj -c Release` builds only this project; packaged sources and scratch/check artifacts are excluded from compilation. `ShockWaveShaderLab.exe --self-test --artifacts <folder>` exercises model/selection/hold/zero/reconnect rules, deterministic recording/independent loop timing/cancellation/memory bounds, parser validation, live loopback OSC reception, actual HLSL compilation/GPU history/pattern/playhead/background-wave readback (including off-page, zero-crossing and row 256 cases), atlas reuse, dynamic label composition, resize and reload recovery, and saves rendered previews/report. `--warp` explicitly checks software rendering. No real device command is sent by any test. The tests also exercise a simulated VRChat peer over real local UDP, including control edges, release ordering, recording/loop feedback, echo filtering, stale input and avatar invalidation. They do not run VRChat or alter the main EXE or real Unity scene.
