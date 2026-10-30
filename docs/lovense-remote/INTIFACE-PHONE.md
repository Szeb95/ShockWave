# Intiface on a phone

Optional local phone bridge, added in source on 2026-10-24. The built-in Windows Bluetooth source remains available. This guide does not describe a published stable update; see STATUS.md for preview-build evidence.

## First connection

1. Put the phone and PC on the same home network. The PC may use Ethernet.
2. Open Intiface Central on the phone. Enable Bluetooth LE and **Listen on all network interfaces**, then start its server. Allow Bluetooth/local-network access when the phone requests it.
3. In ShockWave, open **Settings → Bluetooth toys → Toy source → Intiface / phone**.
4. Select **Find phone**, choose your phone and select **Connect phone**. For discovery, first enable **Broadcast Server Info via mDNS** under Intiface's advanced settings. If discovery finds nothing, enter the server address displayed by Intiface, for example `ws://192.168.1.20:12345`, and select **Connect phone**.
5. Turn on the supported Lovense toys. Their connected functions appear in Toy panel, Toy control and local SPS routing through the existing owner controller. Existing friend sharing can expose supported connected functions through its separate Connect session.

The phone source and selected endpoint are saved separately from existing provider settings. ShockWave reconnects automatically while that source is selected. After discovery-based selection it can rediscover the same advertised host/port following an address change; ambiguous matches are not automatically selected. A manually entered address must remain reachable. Discovery is IPv4 DNS-SD; manual addresses also support IPv6 and `wss://` with normal certificate validation. No Intiface Central desktop installation is needed.

Enable **Start Server when Intiface Central Launches** on the phone for a shorter daily routine: open Intiface, open ShockWave and turn on the toys. This does not start Intiface when the phone boots or guarantee background/screen-lock behavior.

## Names, settings and reconnecting

Give each toy a **unique nickname in Intiface** to retain its phone-source limits and SPS mappings between sessions. The v3 network protocol supplies runtime indexes and optional display names, not the hardware identity used by ShockWave's owned Windows engine. ShockWave therefore does not guess that a reconnecting index is the same physical toy. Unnamed toys and duplicate nicknames get temporary identities and session-only limits; reselect/remap them after reconnecting. A nickname change/collision during a connection closes that session before rebuilding inventory with zero activity. Deliberately reassigning a nickname to different hardware also reassigns its saved settings.

Phone and PC Bluetooth identities remain separate. Existing saved PC toys, limits, mappings and credentials are retained. Changing source or applying a phone connection disarms and closes the previous source first. Discovery alone does not switch servers or send motor commands.

Reconnection restores connections and inventory with output at zero. Fresh manual input or a fresh authorized source is required. Existing sensitivity/maxima, Test mode, source expiry, Stop, scheduling and no command replay remain in force. Phone controls currently use the same supported Lovense scalar/rotation subset as the bundled backend; this does not enable every Intiface brand or motion function.

## Stopping and verification limits

ShockWave checks intent again before submitting a phone command. Ambiguous sends/timeouts abort that socket and disarm the old input; the command is not retried. Normal exit/source change attempts StopAllDevices before closing the client connection, without stopping the phone's server process. The app does not alter the phone's settings.

The phone owns Bluetooth and final physical stopping. Intiface can advertise a protocol ping deadline; ShockWave honors it. Current Intiface Central defaults that deadline to zero (disabled), so ShockWave uses read-only inventory checks to detect connection loss instead. This does **not** create a phone-side deadline during a network blackout, cancel a command already in transit or guarantee a physical stop time. Use Intiface's local Stop if needed. Real phone Wi-Fi, screen locking, Bluetooth latency, battery and physical stopping still need device testing; fake-server checks establish software behavior only.

Discovery/remembered addresses identify a network service, not an authenticated phone. Intiface's usual `ws://` connection is intended for a trusted home network. This feature does not provide Internet rendezvous, change routers/firewalls or replace ShockWave's authenticated friend connection.

## Primary references

- [Intiface phone/direct-access setup](https://intiface.com/docs/intiface-central/brands/lovense/)
- [Intiface server, launch and mDNS settings](https://intiface.com/docs/intiface-central/ui/app-modes-engine-control-panel/)
- [Official DNS-SD contract](https://github.com/buttplugio/buttplug/blob/179b0d9306f519912c10bdddd111686622b7f63a/crates/intiface_engine/src/mdns.rs): `_intiface_engine._tcp`, advertised WebSocket port and `path=/`.
- [Official v3 device fields](https://github.com/buttplugio/buttplug/blob/179b0d9306f519912c10bdddd111686622b7f63a/crates/buttplug_server/src/message/v3/device_added.rs)
- [Intiface Central configuration](https://github.com/intiface/intiface-central/blob/main/lib/bloc/configuration/intiface_configuration_cubit.dart): `serverMaxPingTime` defaults to zero.
