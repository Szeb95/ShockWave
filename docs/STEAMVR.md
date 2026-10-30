# ShockWave wrist and floating panel

The integrated SteamVR panel ships in **ShockWave 0.8.0**. Start SteamVR, open the app and use the headset icon on either control page. Unity and avatar installation are optional legacy integrations, separate from this overlay.

ShockWave 0.7.30 recreates the desktop controls with GDI+ drawing, without using HLSL. The desktop keeps the function list and curve area. Both control pages have Open in VR, Recenter and Stop icons at the bottom left; Toy control adds Record after Stop. The old Clear curve and Stop all buttons are removed. The top toolbar, Play loop, duplicate headings, Touch and Toys/Shockers switch buttons are removed; use the app sidebar to choose Toy control or Shocker control. Shock uses one rectangular button matching Vibrate and Sound. SteamVR keeps the approved wrist/floating design, including its page-switch icon. Both views share connected toys, remote functions, recordings, Test mode, device limits and provider connections. No avatar installation is needed for the SteamVR panel. Existing uploaded avatars remain compatible; this update does not change Unity projects.

In 0.7.17, intensity and duration slider feedback continues during a drag in both layouts, including when rendering is delayed. Dragging requests up to 60 visual updates per second. Release ends the drag; the current values must appear before command buttons accept a new action. Existing pointer and panel smoothing are retained.

## Open it

In 0.7.30, SteamVR input files live in an app-owned stable user-data directory, so removing an obsolete app version cannot remove the bindings in use. Physical file paths handle Windows user-data redirection. The new input registration uses the same included controller mappings; no experimental global input override or game-input capture is enabled. Close older ShockWave VR apps before opening the new version.

In 0.7.29, **Hand side** and **Open as: Wrist/Panel** preview immediately in Settings; **Save settings** keeps the choices for next launch. A tracked free hand can reveal the wrist icon even if its grip binding is unavailable. If the status reports unavailable grip/bindings, use **Settings → SteamVR panel → Open wrist menu** while checking the controller binding. Recovery requires releasing grip before a new pull can start.

After the second Shock press, the confirmation text becomes **Shock sent · 40%** for the submitted capped duration. Several different submitted strengths show a range. In Test mode it says **Test shock**. The wrist uses the same spot below Duration; the other views use their confirmation-status text position.

The desktop Toy control page no longer displays the VRC connection status box in its lower-right corner.

The simple visor outline opens VR. Hover over any desktop footer icon for a small label above it: **Open in VR**, **Recenter**, **Stop all** or **Record**.

The wrist shows **Press again to shock within 3 sec** below Duration and above the icons after the first Shock press. A second released press confirms within the existing three-second window; the prompt disappears when confirmed, expired or cancelled.

Close an older ShockWave app or standalone panel test first. Start SteamVR, open ShockWave, then use the headset icon at the bottom left of Toy control or Shocker control.

The target icon immediately to its right is **Recenter**, available on both desktop pages. It recalls the floating panel in front of your current view through the existing Recall action and releases active input. The pink square **Stop** icon follows Recenter on both pages and retains Stop all behavior. Toy control's Record icon follows Stop. Recenter does not start SteamVR or open a closed overlay.

Under **Settings → SteamVR panel**, choose **Hand side: Left/Right** and **Open as: Wrist/Panel**, then **Save settings**. Panel starts as the floating view; the hand choice is also used when returning to the wrist. Each wrist retains its separate saved fit. Old configurations keep their remembered wrist and start in wrist mode.

Enable **Open in VR when ShockWave starts** and save to open automatically on future launches. Automatic opening waits until SteamVR is running. Closing VR manually cancels any pending automatic opening for this app run. Open in VR, Close VR, Recall and Reconnect VRC are available in the same Settings card.

Point with the free controller and hold its trigger to draw. Drawing continues outside the grid, with the level clamped to its edges, until the trigger releases. With Record enabled, a completed drawing loops through the existing recorder. Each function retains its own recording and owner limits. Up to ten functions appear together; small previous/next buttons appear for additional pages. Functions can stay selected on different pages.

## Place and size it

In 0.7.27, attached-wrist movement uses less position/rotation smoothing (25/30 ms instead of 40/45 ms). Pointer smoothing, floating grips and the opening animation keep their existing settings.

In 0.7.26 the wrist starts **closed**. Bring the other controller within about **18 cm** of the saved wrist anchor to reveal its small Move icon. Release any held grip, then freshly hold grip near the icon and pull toward the top of the watch. The grab zone extends above the overlay (up to about 12 cm directly above it), so the controllers do not need to touch. The icon stays fixed at the wrist anchor. A separate rounded bar with three grip marks appears above it and follows the pull as the menu unfolds. Release to snap fully open (or closed if pulled less than about 40% of the way).

When open, bring the other controller near the top bar to reveal it, then grip near it and pull back to the same wrist anchor to close. The bar uses the floating panel handle's grey/green design. Both the bar and fixed icon have their own proximity visibility: they appear at about **18 cm**, disappear beyond about **24 cm**, and stay visible throughout an owned grip. Moving away hides them while keeping the expanded menu open. These different reveal/hide distances avoid flickering at the boundary. Only the other controller opens or closes the wrist; the trigger and pointer operate its controls once fully open.

**Closing does not stop active waves or submitted shocks.** Waves keep their playback phase, and an already submitted shock keeps its original duration. Closing cancels unfinished drawing/slider input and an unconfirmed Shock prompt. Tracking or focus loss while closed also preserves playback; reopening sends no replay. Explicit Stop, app/VR shutdown, disconnection and source-revocation rules retain their existing behavior.

**Settings → SteamVR panel → Open wrist menu** opens it without the gesture when VR is already running in Wrist mode. **Adjust wrist position** shows the original Move icon below the menu: point at it and hold trigger to move/rotate at the captured pointer distance. Release to save that wrist's fit and return to the normal pull-open handle. The first production run can read a valid saved Test 024 hand fit; later fits save separately in ShockWave's settings folder. Opening/closing never changes that fit.

Expand drops the detailed panel. Touch a side rail and freshly hold the controller grip to pick it up. The other hand can use its pointer. Grip opposite rails with both controllers and pull apart/together to change size. Release either hand to keep the size and continue holding with the other. X returns to the wrist.

Plus and minus change overall scale. Resize reveals the corner cross for changing the grid/shocker area; hold and drag it, then release. Both pages share that size within each placement mode. Grid sizes, dropped positions and recordings remain session-only, as in the approved prototype.

## Transparency

Go to **Settings → SteamVR panel → Transparency**. The slider previews immediately; **Save settings** remembers it. It fades the whole overlay, including icons, labels, cards and grid. At 0% the visible parts are fully shown; at 100% the overlay is hidden and stops accepting input. Restore the slider from the desktop to show it again. Frames and empty areas are already fully transparent at every slider value.

This setting and VR placement/size changes do not fade or rearrange the desktop control screen.

## Shockers and Stop

In SteamVR, the page-switch icon opens the shocker view. Select targets and adjust intensity/duration. Sliders send no command. The Shock icon uses two separate clicks: press and release to arm, then press and release the same icon again within three seconds to confirm. It changes color while armed. A different input source cannot confirm that click. Changes to selection, values, context, availability or Test mode cancel confirmation.

On the desktop, use the rectangular Shock button: press and release to arm, then press it again within three seconds to confirm. It turns green while armed. Holding or dragging cannot submit an extra command or activate Vibrate/Sound. A pending arm from another input source is cancelled rather than confirmed by this button. A held drawing or slider interaction belongs to its starting view; the other view cannot take it over.

Vibrate and Beep use the existing one-shot commands. Each selected target retains its own limits, availability, pause and cooldown. Existing Test mode remains in force. Submitted shocker commands are never queued or replayed. Stop ends continuous toy activity and pending confirmation; a command already accepted by a shocker service can continue until its duration ends. A desktop Stop all button remains available while the overlay is hidden.

## Compatibility and checks

Six default SteamVR controller bindings are included, with the same pointer/grip mappings and smoothing as Test 024. The panel uses GDI+ with a persistent texture copy, with no HLSL renderer in the active desktop/SteamVR view. Existing avatar graphics and OSC feedback remain for compatibility.

Software checks use fake devices, an offscreen desktop and a software graphics device. They verify the production control paths, not physical toy response or headset ergonomics. Integrated headset/controller testing remains the next step. SteamVR must already be running for the separate hidden native diagnostic; the diagnostic never starts SteamVR, reads controller actions or shows overlays.
