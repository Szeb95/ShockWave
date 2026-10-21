# First VR OSC shader-panel demo

This is a separate Editor installer and standalone Shader Lab backend. Main ShockWave integration waits until the user confirms the demo works in VRChat. No toy or remote output is implemented by this demo.

## Add the new version

In the existing Xenas Unity project, select your avatar and open **Tools → ShockWave → Curve Panel → VR OSC Demo**. Click **Add VR OSC Demo to Avatar**. Save the scene yourself when ready. This adds only a new `SzebsCurvePanelVRDemo` child and unique assets under `Assets/SzebsToys/CurvePanelVRDemo/`. Existing avatar objects, the earlier particle prototype, menus, controllers and descriptor assignments remain intact. The new root has a VRCFury Full Controller referencing the generated FX, expressions menu and parameter assets; keep VRCFury for build-time merging. Duplicate installations are refused.

The generator is Editor-only. The uploaded object uses one shader MeshRenderer, VRC Parent Constraint, animation and local self-only Finger contacts. There is no runtime C#, Udon, socket, Unity Canvas or ParticleSystem on the avatar. The board retains the rounded charcoal/green design, function colors, held lines, recorded background waves and touch-following dot. Its bottom is cropped at logical y=708: desktop guidance, compiler/backend status and Open HLSL/Reload/VR OSC buttons are omitted. **Demo OSC stays** for this test.

## Run the first test

1. Open `work/ShaderPanelTest/dist/ShockWaveShaderLab.exe` and click **Start VR OSC**. Keep it open in the background. Alternatively launch with `--vr-demo --demo`.
2. Build & Test/upload your avatar yourself, then enable OSC in VRChat. Open **VR Curve Demo → Show panel**.
3. The badge should become **DEMO CONNECTED**. Touch **Demo OSC** on the board or use the menu's Demo OSC button to populate synthetic toys/functions.
4. Touch a function row to select it. Draw on the graph; ordinary release holds its final value. **Record → touch/draw → release** captures in the program and immediately loops. Deselecting a recorded function preserves its independently timed background wave.
5. **Hold selected**, **Play loop**, **Zero selected**, **Stop all**, **Clear curve** and page arrows work on the board and in the menu. Destination buttons remain visual preview choices.

The panel starts hidden and appears only for its wearer. Show places it in front of the head, waits 200 ms for constraint settling and drops it in world space. Follow head/Recall are available in the menu. The installer does not change finger colliders, avatar descriptor settings or saved scenes. Finger X/Y use box face proximity; actual uploaded-avatar contact/radius/edge calibration is the next user test. If an axis moves backwards, use **Touch test / calibration → Invert finger X/Y**. If finger contacts are unavailable, that menu also offers **Emulate touch** and X/Y radials; these have separate parameters so receiver resets cannot overwrite the menu test.

By default, the backend exclusively listens on **127.0.0.1:9001** and sends feedback to **127.0.0.1:9000**. The existing inventory test socket remains 9002. If another OSC app (including main ShockWave) owns 9001, the demo reports it without taking over or changing that app. Close the competing app for this first standalone test, or launch Shader Lab with `--vrc-receive 9003` and use VRChat's matching `--osc=9000:127.0.0.1:9003` launch argument. `--vrc-send` supports a custom incoming VRChat port. These are explicit launch choices; no VRChat config or launch settings are rewritten.

## GPU performance update

The avatar shader now reads the requested numeric sample directly instead of constructing large dynamically indexed arrays for each pixel. Text is evaluated only for the row/area under that pixel; neighboring curve segments reuse packed sample pairs, and inactive background slots skip curve drawing. The OSC fields, FX, menus, materials, recordings and visual capacity are unchanged.

The installed existing demo's `Panel.shader` and `Panel.cginc` are updated in place, retaining their asset GUIDs. New installs use the same optimized version. **Rebuild & Test or re-upload the avatar to use the updated shader in VRChat.** Removing/reinstalling the panel is unnecessary. Keep Shader Lab open; minimizing it already stops its desktop rendering while OSC/recording/playback continue.

Scratch Unity comparisons use identical numeric feedback at 1488×850, including a stress view with 16 separately phased/colorized loops. Recorded timings measure camera submission plus GPU completion through a blocking one-pixel readback, not isolated GPU time or VRChat FPS. Visual comparisons and the existing Unity checks preserve names/curves/placement/feedback; headset performance still needs the user's retest.

## Colour and text update

The avatar shader now handles the project's Gamma or Linear colour mode explicitly, preserving the desktop's charcoal/green palette without changing project settings. Label/glyph textures have twice the previous resolution, correct transparent edges and no automatic power-of-two resizing. Battery labels render only for populated function rows; empty paging reads 1 / 1. Own/Remote/Both and the other button labels are centered using their actual text bounds.

Both existing generated demo versions receive the shader include and text textures in place, retaining their asset GUIDs, material references, FX, menus, parameters and user placement/scale. New installations generate the same assets. **Rebuild & Test or re-upload the existing avatar** to see the changes; no panel reinstallation is needed. Scratch verification passes 41 checks in each colour mode, including texture import, transparent edges, palette, empty rows and Own centering. The bounded curve rendering from the performance update remains in place.

## Bounded local feedback (contract)

`VrPanelContract.cs` is shared by the host and installer. Version 1 defines 518 numeric feedback floats plus 31 controls/heartbeat parameters: **549 unsynced, unsaved expression parameters**, zero synced bit cost. Input and feedback have separate namespaces to prevent OSC echoes becoming input. All float feedback stays in 0–1. The FX controller maps each numeric value to its shader material property using independent blend trees. Packed pairs use native local float precision, not network-synced floats.

- Six visible function rows with paging through the host's complete bounded inventory; names are 18 ASCII characters per line with `?` for unsupported glyphs. The Windows preview retains complete Unicode names. This is a numeric glyph atlas, not a database of Lovense models.
- Up to 16 active deselected background functions at once, including off-page functions. More remain visible/selectable in the list; an amber dot marks background visual overflow. This bounds the first avatar prototype; it does not reduce the host's 256-function inventory capacity.
- Background clips use 32 preview samples (16 packed level pairs) and the primary curve uses 64 samples. These are visual resamples of the host's original 30 Hz recordings, which remain unchanged. Curves can look less detailed than the Windows 240-point rendering.
- Feedback is sent at up to 20 Hz using small immediate bundles, with periodic refresh and no retry/backlog. Static names/clip data are reused between changes. The first demo uses incremental property updates, not an atomic multi-parameter frame; transient partial visual updates during a page/clip change remain a client test limitation.
- Avatar heartbeat alternates every 500 ms while shown locally. Hidden panel, avatar change, missing heartbeat for two seconds, socket failure or disabling the backend invalidate simulated activity/selection and prevent held-touch replay. Ordinary graph release holds values or commits a valid armed recording. Backend heartbeat loss switches the shader badge/visuals offline after two seconds. Reconnection needs fresh input; no old loop automatically resumes.

Recordings are RAM-only and are cleared when Shader Lab closes. Own/Remote/Both does not connect to toys or grant remote access. Production source arbitration, permissions, hardware scheduling, stopping and integration are still deferred.

## Verification and preservation

Only requested Editor tool files are installed into the real project. No real scene, prefab, package, project setting, remembered scene/editor state or unrelated tool is modified. The agent does not run this verifier against the real avatar project; installation is an explicit user action. The generator saves only its own new assets, not all dirty project assets.

`VrPanelVerification.Run` rejects execution outside the separate `work/CurvePanelVRDemo/checks/unity-project/` scratch project. It checks generated assets/FX/menus/parameters, local contacts, packed Animator feedback, placement/freshness and a rendered panel. Windows self-tests use real local UDP with a simulated VRChat peer. These checks are distinct from the user's uploaded-avatar/headset/finger/OSC test, which remains required before main ShockWave integration.

Official references: [VRChat OSC avatar parameters](https://docs.vrchat.com/docs/osc-avatar-parameters), [OSC ports and enablement](https://docs.vrchat.com/docs/osc-overview), [native local float precision and parameter limits](https://creators.vrchat.com/avatars/animator-parameters/), [contact receivers and face proximity](https://creators.vrchat.com/common-components/contacts/).
