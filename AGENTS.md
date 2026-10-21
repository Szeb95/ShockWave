# Project guidance

## User's strict Unity preservation rule

- The user explicitly requires everything in the avatar Unity project to stay unchanged except the requested Szebs Toys Editor tools and Szebs Toys content.
- Scope Unity file edits to the relevant Szebs Toys tool folders under `Assets/Editor/` and generated content under `Assets/SzebsToys/`. Preserve existing tool/content files outside the requested change.
- Do not modify, move, delete or automatically save existing scenes, avatars, prefabs, other assets, packages, project settings or editor preferences. Preserve loaded scenes, unsaved work, scene selection, layout and remembered open-scene state.
- Do not run Unity batch verification/import against the real avatar project: it can change cached editor state even when the verifier uses a synthetic preview scene and never saves an avatar scene. Use a separate scratch project for Unity verification, or read-only/standalone checks that do not launch the real project's Editor.
- Installing the panel on an avatar remains an explicit user action through the Editor tool. It must add only the requested Szebs Toys content and preserve existing avatar objects, components, menus and controller assets. Do not perform automatic installation, scene switching or scene saving on the user's behalf.
- Before any later action requires changes outside this scope, explain the concrete necessary change and obtain explicit user authorization. Existing authorization to build a tool does not authorize unrelated Unity changes.

## Existing application

- `overlay/` is a native Windows .NET 8 WinForms application.
- `bridge/` is a Node.js ESM OSC / OpenShock bridge.
- The overlay launches the bridge and sends JSON lines over stdin; the bridge reports logs and structured status over stdout.
- Read the relevant existing README and source before changing behavior. Preserve current configuration migration, test mode, token storage, per-device limits, and the existing OpenShock no-retry/no-replay behavior.
- Do not assume browser WebRTC APIs exist in the WinForms or Node runtime.

## Lovense / remote workstream

Before working on local toys, Lovense integration, relay services, remote clients, or SPS mappings, read:

1. `docs/lovense-remote/README.md`
2. `docs/lovense-remote/ARCHITECTURE.md`
3. `docs/lovense-remote/PROTOCOL.md`
4. The matching brief in `docs/lovense-remote/chats/`
5. `docs/lovense-remote/STATUS.md`

These files are the shared handoff between implementation chats. Update STATUS.md with actual changes, checks, limitations, and the next dependency. Distinguish planned behavior, mock verification, and physical-device verification.

The agreed starting constraints are: Szebs on both ends, no required standalone Lovense Remote or Intiface app, and EUR 0 recurring hosting budget. Native Lovense SDK/BLE integration is the first candidate; bundled Buttplug remains an alternative. Metered/Open Relay is a candidate service, not a confirmed dependency. Verify the actual free entitlement and behavior at exhaustion before relying on it.

The draft protocol is a coordination starting point, not shipped functionality. Keep it synchronized with implementation; changes to shared contracts must be documented for the other chats.

Never commit real tokens, invite secrets, provider administrator keys, or private hardware identifiers. Existing work in this checkout must not be discarded.
