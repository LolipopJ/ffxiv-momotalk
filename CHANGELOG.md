# Changelog

This file records notable changes to FFXIV MomoTalk. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and version headings use [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added
- Automatic merging of consecutive messages sent by the same user in the same conversation within 500 ms (e.g., FFXIV chat macros) into a single newline-separated chat message.
- OpenAI-compatible AI reply suggestions via a conditional sparkle button in the chat send bar, generating 1–3 single-line replies from timestamped recent conversation context (defaulting to the last 10 minutes, up to 100 messages, minimum 5) and notes into an in-timeline MomoTalk-styled `Reply` card with click-to-send and automatic dismissal when sending manually.
- Editable inline notes for groups (Free Company, Linkshells, Cross-World Linkshells, and Statics) in the `Groups` detail card, replacing the default group description in the `Groups` list when set and supporting search by note.

### Changed
- Sized chat timeline avatars to span from the top of the sender name to the bottom of the first single-line message bubble.
- Refactored the Settings modal into `General` and `AI Reply` tabs with scrollable narrow-window support, tactile button hover/press feedback, LLM Endpoint, masked/toggleable API Key, Model, `Test Connection` endpoint check, configurable `Chat Context` window/limits, editable System Prompt, and `Reset to Default`.
- **Breaking:** Split per-character SQLite storage into four independent domain databases under `characters/{ContentId}/` (`messages.sqlite3`, `notes.sqlite3`, `statics.sqlite3`, and `cache.sqlite3`) with independent `user_version` schemas so future cache or state schema updates never require resetting chat history, user notes, or static rosters; legacy `history/{ContentId}.sqlite3` files are preserved unmodified without migration.

### Fixed
- Cached Free Company, Linkshell, and Cross-World Linkshell group channels per character immediately on first fetch, loading them on startup and preserving existing groups when native proxies are empty (e.g., on relog, reload, zone transitions, or cross-world duties).
- Hardened Friends list caching by adopting already-populated native friend buffers on attach and guarding against empty or late cache overwrites.

## [v0.2.0.0] - 2026-11-23

### Added
- Free Company (`/fc`), Linkshell 1–8 (`/l1`–`/l8`), and Cross-World Linkshell 1–8 (`/cwl1`–`/cwl8`) group chat capture and sending, using stable channel keys (FC `Id`, Linkshell `Id`, CWLS `FoundationTime`) so history is preserved across leave/rejoin, slot moves, and renames.
- Static party group chats (`/p`) with exact 1:1 member matching across regular, in-duty, and cross-world parties, supporting multiple statics and renaming.
- Native in-game Party List context menu entry (`Create Static Group` / `Open Static Group`) and MomoTalk-styled Create/Rename Static modals.
- `Groups` tab (between `Friends` and `Chats`) with active FC, Linkshell, Cross-World Linkshell, and saved Static list plus a Group Detail Card showing static member avatars and live party match status.
- Per-speaker Lodestone avatars in group chat timelines (with `(World, Name)` cache fallback when `ContentId` is unavailable) and `Speaker: message` previews in the Chats list.

### Changed
- **Breaking:** Upgraded per-character SQLite history schema to `user_version` 3 for unified private/group conversations and static rosters; unrecognized v2 databases are preserved unmodified without migration.
- Expanded wide left rail and narrow bottom navigation to three tabs (`Friends`, `Groups`, `Chats`) using opaque blended icon colors to prevent semi-transparent overlap seams.
- Show right-aligned `You` above outgoing bubbles (and typing indicator) when `Show Self Avatar` is enabled.
- Switched the GitHub Actions release workflow from tag-push triggers to manual `workflow_dispatch` with atomic version bump, build, tag, and release publishing.

### Fixed
- Preserved the active character session and cached Friends list across loading screens, zone list resets, and cross-world duties instead of clearing `cached_friends`.

## [v0.1.0.0] - 2026-11-22

### Added
- Initial MomoTalk in-game messenger (`/momotalk`) for FINAL FANTASY XIV Global (Windows x64, .NET 10, Dalamud API 15) with real friend `/tell` capture, sending, and per-character SQLite history keyed by immutable `ContentId`.
- Blue Archive MomoTalk-styled frameless window with wide 4-pane and narrow single-pane responsive layouts, `Friends` and `Chats` tabs, search, `Recent`/`Name` asc/desc sorting, grouped bubbles, time dividers, typing indicator, and unified right-click menus (`Open Lodestone`, `Delete Chat`, `Copy Text`).
- Per-character cached Friend List refreshed on opening the native Friend List, editable inline friend notes, and daily revalidated Lodestone avatars with live texture reload.
- Draggable floating entry button (`entry.png`) with persisted position, cover bounce opening splash (`momotalk-cover.png`), bundled Dalamud plugin icon (`images/icon.png`), and MomoTalk settings modal (`Show Floating Entry`, `Show Intro Animation`, `Show Self Avatar`).
- GitHub custom plugin repository (`pluginmaster.json`), D17/Plogon packaging metadata, English and Simplified Chinese READMEs, and automated release workflow.

### Fixed
- Attached native in-game outgoing Tell rejection errors to matching message bubbles or conversation banners.
- Hidden the floating entry button while logged out or on the title/character select screens.

[unreleased]: https://github.com/LolipopJ/ffxiv-momotalk/compare/v0.2.0.0...main
[v0.2.0.0]: https://github.com/LolipopJ/ffxiv-momotalk/compare/v0.1.0.0...v0.2.0.0
[v0.1.0.0]: https://github.com/LolipopJ/ffxiv-momotalk/commits/v0.1.0.0
