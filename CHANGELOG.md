# Changelog

This file records notable changes to FFXIV MomoTalk. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and version headings use [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [v1.0.0.2] - 2026-11-23

### Changed
- Automatically roll `CHANGELOG.md` `[Unreleased]` entries into the released version section and update compare links in the release workflow.

## [v1.0.0.1] - 2026-11-24

### Fixed
- Move the release tag and GitHub Release target to the `chore(release)` commit created by the release workflow instead of leaving it on the previous commit.

## [v1.0.0.0] - 2026-11-24

### Added
- Group consecutive messages from the same sender within 500 ms into a single timeline bubble.
- OpenAI-compatible AI reply suggestions via a conditional sparkle button in the chat send bar, generating 1–3 single-line replies from timestamped recent conversation context (defaulting to the last 10 minutes, up to 100 messages, minimum 5) and notes into an in-timeline MomoTalk-styled `Reply` card with click-to-send and automatic dismissal when sending manually.
- Editable inline notes for groups (Free Company, Linkshells, Cross-World Linkshells, and Statics) in the `Groups` detail card, replacing the default group description in the `Groups` list when set and supporting search by note.

### Changed
- Sized chat timeline avatars to span from the top of the sender name to the bottom of the first single-line message bubble.
- Added a close button to dismiss `error` banners above the chat box while keeping persistent `info` reminders (such as a Static group requiring all members in the party to send messages) non-dismissible.
- Refactored the Settings modal into `General` and `AI Reply` tabs with scrollable narrow-window support, tactile button hover/press feedback, LLM Endpoint, masked/toggleable API Key, Model, `Test Connection` endpoint check, configurable `Chat Context` window/limits, editable System Prompt, and `Reset to Default`.
- **Breaking:** Split per-character SQLite storage into four independent domain databases under `characters/{ContentId}/` (`messages.sqlite3`, `notes.sqlite3`, `statics.sqlite3`, and `cache.sqlite3`) with independent `user_version` schemas so future cache or state schema updates never require resetting chat history, user notes, or static rosters; legacy `history/{ContentId}.sqlite3` files are preserved unmodified without migration.

### Fixed
- Recorded and attached message send failure notices directly below the failed message bubble in the timeline so they scroll with the chat history instead of remaining above the chat input box.
- Resolved the local player's full identity and `ContentId` in Static party chats (and other group chats) when FFXIV's in-game log Name Display Type is set to an abbreviated format (`Forename S.`, `F. Surname`, or `F. S.`), displaying self messages on the right with the full name and Lodestone avatar.
- Preserve cached group channels while native membership is unavailable. Clear confirmed departures and require current membership and slots to send.
- Hardened Friends list caching by adopting already-populated native friend buffers on attach and guarding against empty or late cache overwrites.
- Keep drafts when a send is blocked before it reaches the game and preserve edits made while dispatch is pending.
- Attach game rejections to the matching outgoing message, refresh merged lines in open timelines, and sort chats by their latest activity.
- Keep failed note and static group edits available for retry and update the displayed values after saving.
- Use an ID index for live message updates and reuse bubble measurements until their text, width, or font changes.
- Retry AI requests only when the provider reports that it cannot use `response_format`.
- Verify the published ZIP before updating the plugin catalog and resume interrupted releases for the same tag and commit.
- Inject `IconUrl` into the packaged plugin manifest during custom repository releases so installed plugins display the MomoTalk icon in Dalamud.

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

[unreleased]: https://github.com/LolipopJ/ffxiv-momotalk/compare/v1.0.0.2...main
[v1.0.0.2]: https://github.com/LolipopJ/ffxiv-momotalk/compare/v1.0.0.1...v1.0.0.2
[v1.0.0.1]: https://github.com/LolipopJ/ffxiv-momotalk/compare/v1.0.0.0...v1.0.0.1
[v1.0.0.0]: https://github.com/LolipopJ/ffxiv-momotalk/compare/v0.2.0.0...v1.0.0.0
[v0.2.0.0]: https://github.com/LolipopJ/ffxiv-momotalk/compare/v0.1.0.0...v0.2.0.0
[v0.1.0.0]: https://github.com/LolipopJ/ffxiv-momotalk/commits/v0.1.0.0
