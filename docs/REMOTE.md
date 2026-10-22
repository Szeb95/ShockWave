# Connect to another ShockWave app

Use the **Connect** page between **SPS** and **Log**. Both people run this version of ShockWave. Commands go to the other app; that app operates its own Bluetooth toys and configured shocker service. Your app never attempts a Bluetooth connection to your friend's toys.

## Saved friends (0.7.7 and later)

Both people need the saved-friends update. Choose **Pair a new friend using a code**, connect using the steps below, optionally enter a local friend name, and press **Save friend** on either app. The authenticated connection saves a separate pairing key on both apps. Each person can rename their own entry; names are not shared. Up to 32 friends can be remembered, with one active connection at a time.

Next time, choose the saved friend and press **Connect** on both apps. No new code is needed after restarting or after the original code expires. Connect defaults **off** after an app restart. While both remain enabled, the selected friends retry automatically. **Disconnect** stops the session while retaining the pairing. **Forget friend** disconnects the active session and removes that saved access; pairing again requires a fresh code. The other app may retain its own entry, but the old key and retired original code cannot reconnect to the person who forgot it.

The stable app identity, names and per-friend keys are protected by Windows for the current user in `%LOCALAPPDATA%/SzebsToys/remote-friends.bin`, separately from provider tokens. Existing keys cannot be silently replaced by a new pairing. Unreadable data is preserved and Connect stays off; failed saves are reported. An interrupted/unconfirmed save can leave a local entry: if it cannot connect, forget it and pair again using a fresh code. Do not copy the protected file between Windows users or PCs.

Saved pairing uses the existing public setup helper, STUN and direct encrypted traffic, without accounts, own hosting or TURN. The setup helper can see the stable setup identity across app restarts; direct peers still see each other's address. Reconnection restores inventory with fresh session aliases and zero activity. Drawing/Play or a new shocker action is still required; limits, owner Stop, Test mode and the 500/250 ms freshness rules remain unchanged.

## Connect

1. Choose **Pair a new friend using a code**. One person leaves **Friend's connection code** empty and selects **Connect**.
2. They select **Copy my code** and send it privately to their friend.
3. The friend pastes it into **Friend's connection code** and selects **Connect**.
4. Wait for **Connected · encrypted direct app-to-app**. Each person's connected supported functions appear together in **Toy control**, with remote names starting with **Remote**. Configured remote shockers appear in **Shocker control**. Connecting starts no output.

Connect is the session's authorization to use connected supported functions and configured unpaused shockers, within the receiving owner's existing limits. There are no additional per-toy approvals. Each direction works independently; a person does not have to operate their friend's toys in return. Sharing a code does not start a motor or a shock: fresh drawing/Play or a later shocker action is still required.

Either person can use **Disconnect** at any time. This removes remote inventory, clears remote input and ends peer-owned output leases. Local owner input is independent. Turn Connect back on to reconnect; while the app/code remain valid, the other side can retry automatically if its Connect remains on. Reconnection uses fresh session aliases and starts with zero activity, without restoring patterns or commands. Restarting ShockWave creates a new code. Codes expire for new connections after 24 hours; keep them private.

The sharing code appears after selecting **Connect**, before the setup service connects; your friend does not need to be online yet. If **Connection helper stopped · use Connect to try again** appears with no code in the original 0.7.0 package, use the complete corrected 0.7.1 package. Some connection-library files were bundled inside the EXE where the separate Node helper could not load them. This startup failure is independent of IPv4, router settings and your friend's network. Extract the whole ZIP, keeping its `bridge` folder beside the EXE.

## Toys and shockers

Remote toy functions use the existing desktop/VR Toy control drawing, Record and Play behavior. Locally connected functions are shared, with up to 32 toys/eight supported scalar functions per toy. Unknown functions and depth/position settings are not shared. Incoming input goes through the existing receiver controller, Sensitivity/Max %, Test mode, scheduler, source ownership, Stop and expiring leases. A receiver edit/Stop invalidates the held peer claim; the sender needs fresh input to take over again. A connection grants no ability to edit the owner's settings, credentials or PC.

Remote toys appear in Toy control while they remain in the connected friend's inventory. Turning off/removing a friend's toy or disconnecting removes its controls immediately, including while the drawing/OSC timer is paused; other local and remote functions keep their input. Reappearing toys are unselected at zero, without restoring activity. SPS lists and configures only your local toys, never a friend's toys.

From 0.7.2, SPS yields only the functions currently controlled by a live desktop, avatar or remote drawing/loop, including zero values within that live input. Other SPS functions continue normally. When interactive control ends, that function can resume SPS from a new contact update received afterward; cached positive input is not restored. No reconnect or SPS switch reset is needed. Explicit owner Input/Stop still overrides control and requires release before SPS can rearm. Consecutive fresh remote gestures replace the same live source without an unnecessary stop between them. Update both apps for consistent behavior.

From 0.7.3, continuous toy updates have a **500 ms total freshness limit**, including network age, clock uncertainty and receiving-app delay, to tolerate ordinary Internet jitter. The previous 250 ms limit could expire a drawing after a brief delay while Connect stayed Connected. Discrete shocker commands retain their **250 ms** limit. If a toy claim does expire or is rejected, its activity/loop stops but its row stays selected; release and draw again, or press Play, to restart. Selection alone and a held press cannot restart it. Disconnect still removes remote inventory, and Stop all still clears selection. Both apps should update. Hardware/driver delays can add to physical stopping time; these are software deadlines, not measured motor feedback.

The **Shocker control** tab and avatar **Shockers** page list local and remote configured shockers. Remote names start with **Remote**. Choose one shocker and use the left Intensity / Duration sliders (0–100%, 0.3–15 s). Moving a slider sends no command. Vibrate and Sound submit once. For Shock, press the **right green ARM circle**, release, then press the **left green SHOCK circle within three seconds**. Every shock requires both steps again. Changes to the selected shocker, values, page or connection cancel confirmation. Test mode blocks remote sending.

The receiving shocker bridge must be running and ready. Remote actions use the owner's other-controller limits, pause, cooldown, provider/share permissions, freshness and no retry/replay. An already accepted action can continue for its bounded duration; Stop cancels pending confirmation and toy activity, not an already accepted shock. Selection of one local shocker provider at a time remains unchanged. The avatar requires the updated Toy Control Editor tool and an explicit Update/upload before VR shocker input is enabled.

## Setup helpers, without TURN

- **PeerJS Cloud** exchanges connection details, using its public free signalling service without a hosting account. It does not carry toy inventory or commands. This is a shared service with no availability guarantee.
- **Google STUN** helps discover public addresses. ShockWave tests direct IPv4/IPv6 routes automatically. No router/firewall setting is changed automatically.
- **TURN is not configured or used.** There is no relay fallback or relay allowance. If the networks cannot establish a direct route, ShockWave remains waiting/retries with no remote output. A different network or deliberate router/firewall setup may be needed.

Both helpers see connection metadata such as addresses. Direct peers can see each other's public address. Signalling offers are authenticated with the private invitation before a connection is allocated; the encrypted DTLS data channel is independently authenticated against its actual fingerprints. Provider API keys stay in their existing local encrypted storage and are removed from the connection worker's environment. The remote transport uses pinned MIT-licensed werift 0.25.1 and ws 7.5.13 in the bundled Node runtime. Their licenses/dependency notices are included in bridge/node_modules.

State uses approximately 30 network updates per second, independently of the receiving toy's existing Bluetooth rate. Delayed/old/duplicate/wrong-session state is rejected, pending values are bounded, and hardware-source leases expire without a UI/network tick. Heartbeats and Windows process ownership prevent an abandoned helper from accepting input after the app exits. This is software behavior; a hung hardware driver, toy or PC can prevent a physical stop from succeeding.

## Diagnose a toy that stops while Connect stays Connected (0.7.5)

Update both apps. After the next stop, use **Log → Copy remote log** on both apps and compare the events around that time. Fresh drawing/Play restarting without reconnecting suggests a stopped input claim; it does not prove the network disconnected. This diagnostic update does not increase the 500 ms toy or 250 ms shock freshness deadlines or automatically restart expired activity.

Logs identify receiver expiry, another input, owner/safety resets, controller replacement/unavailability, the sender receiving a rejection, local drawing-heartbeat expiry, delayed app ticks and an actual peer/worker connection loss. Five-second network summaries contain sent/received/drop counts, maximum packet age/gap, minimum remaining lifetime, maximum RTT/clock uncertainty and app update gaps. These are software observations, not physical motor feedback. Events are rate limited and contain no connection code, IP address, device/session alias, hardware ID or raw peer error text; rejection text is allowlisted. Copy remote log includes the app version and capture time while excluding other activity categories. Existing log rotation applies. Older logs have no remote-control trace, so they cannot establish this remaining stop's cause. The graph heading/new history samples now follow selected stopped values back to zero instead of displaying the last drawn percentage.

## Verification limits

Version 0.7.6 fixes a confirmed Log-page stall. The old view measured every stored message when opening/resizing and could delay control updates long enough to expire a remote input while the peer stayed connected. The Log view now draws rows on demand at a fixed height and avoids hidden-page scrolling/layout work. Hover or either Copy button still provides full messages; Clear and the 500-entry limit remain. An isolated 500-row test reproduced expiry before the fix and preserved a 330 ms simulated input lease afterward. Real packet gaps can still trip the unchanged freshness rules and require fresh drawing/Play; this change is not an automatic resume or a longer timeout.

Verified with two isolated native sessions on the same Windows PC using the real public signalling helper and encrypted direct WebRTC, four simulated toys in both directions, receiver caps/Stop/expiry/Test mode, disconnect and fresh reconnection. This does not establish success across two separate household/mobile networks or physical toy/shocker response and stop timing. No live avatar, existing Unity project, real toy, shocker account credential or physical command was used during these checks. Try the connection and Test mode first; report the displayed status, never a connection code or API key.
