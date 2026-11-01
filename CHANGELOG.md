# Changelog

All notable changes to this project are documented here. Versioning follows [Semantic Versioning](https://semver.org/).

## [1.3.0] — 2026-11-01

### Fixed

- Release builds are fully silent: tracker file logging now compiles only under `#if DEBUG` (previously `rzswitch.log` grew unbounded); service logging was already DEBUG-only. Policy documented in `AGENTS.md`.
- Tracker identity unified to `rztracker.exe --foreground-tracker` everywhere: web `own-install` no longer spawns a rogue second service copy, `own-remove` unregisters autostart, autostart lifecycle is install/remove only (stop no longer strips it).
- Setup no longer freezes: install/remove run off the UI thread with buttons locked and progress in the log; `Remove from system` kills a running tracker (including the old-format one) before deleting files and reports locked leftovers truthfully instead of a false "removed".
- Setup panel no longer pops a CMD window (`AllocConsole` only for CLI `--install`/`--uninstall`); `deploy-*.ps1` stage the single-file setup exe plus the tracker.

### Added

- `CopyTrackerExe` publish target: `rztracker.exe` is auto-copied next to `rzswitch.exe` on every publish; release checklist in `VERSIONING.md` now covers all three exes.

## [1.2.0] — 2026-10-30

### Added

- Automatic protection against Razer GameManager coming back: every 30 seconds the service checks it; if Razer restarted it (e.g. after an update), rzswitch stops it, keeps it disabled, and restores profile switching. Every action is written to the log.

## [1.1.0] — 2026-10-30

### Added

- System install in the setup window: copies both files to AppData, registers rzswitch in Windows Programs, and supports uninstall straight from the Programs list (with a completion dialog).

### Changed

- Installing to the system first stops and unregisters the service running from the current folder.
- The Install button deactivates while the system service is running.

## [1.0.0] — 2026-10-30

### Added

- Drop-in replacement for the Razer GameManager service: detects the active game and tells Synapse to switch profiles. Lighter, no log spam, no missed launcher-based games.
- Local web control panel for service management and a live log.
- Graphical setup: install, remove, start, and stop the service; turn Razer's GameManager off and on.
