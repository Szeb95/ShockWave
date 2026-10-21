# Connect to another ShockWave app

Use the **Connect** page between **SPS** and **Log**. Both people run this version of ShockWave. Commands go to the other app; that app operates its own Bluetooth toys and configured shocker service. Your app never attempts a Bluetooth connection to your friend's toys.

## Connect

1. One person leaves **Friend's connection code** empty and selects **Connect**.
2. They select **Copy my code** and send it privately to their friend.
3. The friend pastes it into **Friend's connection code** and selects **Connect**.
4. Wait for **Connected · encrypted direct app-to-app**. Each person's connected supported functions appear together in **Toy control**, with remote names starting with **Remote**. Configured remote shockers appear in **Shocker control**. Connecting starts no output.

Connect is the session's authorization to use connected supported functions and configured unpaused shockers, within the receiving owner's existing limits. There are no additional per-toy approvals. Each direction works independently; a person does not have to operate their friend's toys in return. Sharing a code does not start a motor or a shock: fresh drawing/Play or a later shocker action is still required.

Either person can use **Disconnect** at any time. This removes remote inventory, clears remote input and ends peer-owned output leases. Local owner input is independent. Turn Connect back on to reconnect; while the app/code remain valid, the other side can retry automatically if its Connect remains on. Reconnection uses fresh session aliases and starts with zero activity, without restoring patterns or commands. Restarting ShockWave creates a new code. Codes expire for new connections after 24 hours; keep them private.

The sharing code appears after selecting **Connect**, before the setup service connects; your friend does not need to be online yet. If **Connection helper stopped · use Connect to try again** appears with no code in the original 0.7.0 package, use the complete corrected 0.7.1 package. Some connection-library files were bundled inside the EXE where the separate Node helper could not load them. This startup failure is independent of IPv4, router settings and your friend's network. Extract the whole ZIP, keeping its `bridge` folder beside the EXE.

## Toys and shockers

Remote toy functions use the existing desktop/VR Toy control drawing, Record and Play behavior. Locally connected functions are shared, with up to 32 toys/eight supported scalar functions per toy. Unknown functions and depth/position settings are not shared. Incoming input goes through the existing receiver controller, Sensitivity/Max %, Test mode, scheduler, source ownership, Stop and expiring leases. A receiver edit/Stop invalidates the held peer claim; the sender needs fresh input to take over again. A connection grants no ability to edit the owner's settings, credentials or PC.

From 0.7.2, SPS yields only the functions currently controlled by a live desktop, avatar or remote drawing/loop, including zero values within that live input. Other SPS functions continue normally. When interactive control ends, that function can resume SPS from a new contact update received afterward; cached positive input is not restored. No reconnect or SPS switch reset is needed. Explicit owner Input/Stop still overrides control and requires release before SPS can rearm. Consecutive fresh remote gestures replace the same live source without an unnecessary stop between them. Update both apps for consistent behavior.

The **Shocker control** tab and avatar **Shockers** page list local and remote configured shockers. Remote names start with **Remote**. Choose one shocker and use the left Intensity / Duration sliders (0–100%, 0.3–15 s). Moving a slider sends no command. Vibrate and Sound submit once. For Shock, press the **right green ARM circle**, release, then press the **left green SHOCK circle within three seconds**. Every shock requires both steps again. Changes to the selected shocker, values, page or connection cancel confirmation. Test mode blocks remote sending.

The receiving shocker bridge must be running and ready. Remote actions use the owner's other-controller limits, pause, cooldown, provider/share permissions, freshness and no retry/replay. An already accepted action can continue for its bounded duration; Stop cancels pending confirmation and toy activity, not an already accepted shock. Selection of one local shocker provider at a time remains unchanged. The avatar requires the updated Toy Control Editor tool and an explicit Update/upload before VR shocker input is enabled.

## Setup helpers, without TURN

- **PeerJS Cloud** exchanges connection details, using its public free signalling service without a hosting account. It does not carry toy inventory or commands. This is a shared service with no availability guarantee.
- **Google STUN** helps discover public addresses. ShockWave tests direct IPv4/IPv6 routes automatically. No router/firewall setting is changed automatically.
- **TURN is not configured or used.** There is no relay fallback or relay allowance. If the networks cannot establish a direct route, ShockWave remains waiting/retries with no remote output. A different network or deliberate router/firewall setup may be needed.

Both helpers see connection metadata such as addresses. Direct peers can see each other's public address. Signalling offers are authenticated with the private invitation before a connection is allocated; the encrypted DTLS data channel is independently authenticated against its actual fingerprints. Provider API keys stay in their existing local encrypted storage and are removed from the connection worker's environment. The remote transport uses pinned MIT-licensed werift 0.25.1 and ws 7.5.13 in the bundled Node runtime. Their licenses/dependency notices are included in bridge/node_modules.

State uses approximately 30 network updates per second, independently of the receiving toy's existing Bluetooth rate. Delayed/old/duplicate/wrong-session state is rejected, pending values are bounded, and hardware-source leases expire without a UI/network tick. Heartbeats and Windows process ownership prevent an abandoned helper from accepting input after the app exits. This is software behavior; a hung hardware driver, toy or PC can prevent a physical stop from succeeding.

## Verification limits

Verified with two isolated native sessions on the same Windows PC using the real public signalling helper and encrypted direct WebRTC, four simulated toys in both directions, receiver caps/Stop/expiry/Test mode, disconnect and fresh reconnection. This does not establish success across two separate household/mobile networks or physical toy/shocker response and stop timing. No live avatar, existing Unity project, real toy, shocker account credential or physical command was used during these checks. Try the connection and Test mode first; report the displayed status, never a connection code or API key.
