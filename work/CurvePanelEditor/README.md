# Avatar curve panel editor tool

Installed in the same Unity project as the earlier PiCtrl editor tool: `C:/creator companion projekte/Xenas 3 multi avi/Assets/Editor/SzebsToysCurvePanel/`.

## Add it to an avatar

1. Open that project in Unity and let scripts finish importing.
2. Select your avatar in the loaded scene.
3. Open **Tools → ShockWave → Curve Panel → Add to Avatar**.
4. Choose distance/below-eye placement and click **Add Curve Panel to Avatar**.
5. Save the scene. The new empty `SzebsCurvePanel` root has a VRCFury Full Controller with FX, menu and parameter assets; the visible geometry/contacts are under `Panel`. Keep VRCFury on the avatar for build-time merging.

The editor shows the panel for positioning. In VRChat it starts hidden; **Curve Panel → Show panel** places it in front of the head and then freezes it in world space. **Recall in front** places it again, and **Follow head** keeps it attached to the head instead. Show/hide can also be used to place it again. The whole system is local to the wearer.

Touch the graph with your own finger. Height drives the current point; an eight-second particle trace scrolls left. The thin top-right lamp lights during contact. If contacts are disabled in the avatar's descriptor/client settings, restore finger contacts for the VRChat test. A **Preview level** radial provides a visual fallback when there is no finger contact; it has its own parameter and is not an actuator request.

**Clear curve** and **Stop** clear the visual trail while held, then the live visual test continues. Stop additionally exports its own OSC flag for the future app gate; this prototype does not connect to hardware. Destination selects Own, Remote or Both and updates the highlighted label. It does not establish a remote session or grant permission.

## Implementation and boundaries

- Unity 2022.3 editor tool, VRChat SDK with Box/Use Face Proximity contacts, and the installed VRCFury public API.
- No custom runtime script, Udon, web view, socket, toy command or account operation on the avatar.
- Two local self-only Finger receivers: one thin touch gate and one rotated box face-proximity height sensor. Touch and level are exported separately. Actual finger-radius/edge calibration still needs the uploaded-avatar test.
- Five FX layers: local placement/visibility, touch versus preview height, history reset, contact feedback and destination feedback. All parameters are unsynced/unsaved with zero defaults; global names prevent VRCFury renaming the OSC contract.
- Up to 260 particles with an eight-second lifetime. Custom simulation space is the panel, so changing emitter height preserves old samples and repositioning the panel moves the existing trace with it. This is a bounded visual trace, not saved/editable pattern playback or motor telemetry.
- Labels/grid are baked into a generated texture; the upload contains MeshRenderers, contacts, a VRC constraint and a ParticleSystem. It does not depend on runtime Unity UI/TextMesh components.
- Existing PiCtrl objects/assets and the avatar descriptor's menu/controller assets are preserved. VRCFury merges the additional assets. Duplicate installs are refused. Failed builds remove only their own new object/unique asset folder; a successful install supports scene Undo. Undo may leave the generated asset folder for later reuse/review.

The actual OSC names/types and required future receiving behavior are in [PROTOCOL.md](../../docs/lovense-remote/PROTOCOL.md). The app's continuous/remote routing is a separate pending workstream.

## Checks

The user requires preservation of all existing avatar/project content and editor state. Changes in the real Unity project are restricted to the requested ShockWave Editor tools and generated ShockWave assets. The user explicitly runs avatar installation; the agent must not switch/save existing scenes or alter unrelated content/settings. Run Unity verification in a separate scratch project, never the real avatar project: even a synthetic preview-scene verifier can change the project's remembered open-scene state when Unity starts/exits.

`CurvePanelVerification.Run` is an explicit batch entry point, not an automatic import action. It builds a temporary synthetic avatar in an isolated preview scene, checks menus/parameters/contact filters/FX transitions, simulates retained particle history and renders a preview to the absolute path in `SHOCKWAVE_CURVE_PREVIEW`. It removes its unique test assets and does not save avatar scenes. Imported/generated assets and simulated controller behavior are distinct from an uploaded VRChat/finger/hardware test.
