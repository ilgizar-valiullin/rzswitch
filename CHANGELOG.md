# Changelog

All notable changes to this project are documented here. Versioning follows [Semantic Versioning](https://semver.org/).

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
