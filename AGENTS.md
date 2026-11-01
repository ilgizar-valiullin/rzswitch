# rzswitch agent notes

## Logging policy (do not regress)

- Release builds must be silent: no log files, no console output in normal paths.
- Tracker file logging (`FileLogger` in `TrackerLoop`, `Program.cs`) is compiled
  only under `#if DEBUG`. Reason: `rzswitch.log` would otherwise grow unbounded
  on user machines.
- Service DI logging (`AddConsole` + `FileLogger` provider) is likewise
  `#if DEBUG`-only; `EnableDetailedErrors` too.
- Setup writes `[DEBUG]` lines only to an attached console (CLI `--install` /
  `--uninstall` modes via `AllocConsole`); the GUI panel itself never opens
  a console window.
- Consequence: the `/api/log` viewer shows content only from DEBUG builds.
  In release it reports a missing file — that is expected, do NOT "fix" it
  by re-enabling release logging.

## Release flow

- See `VERSIONING.md`: git tag is the single source of truth (`vX.Y.Z`).
- `rzswitch.csproj` keeps hardcoded `3.16.0.0` versions intentionally
  (Razer-compatibility mimicry for Synapse) — this is an exception to the
  "no hardcoded Version" rule, do not remove.
- Tracker binary is `rztracker.exe` (same binary as `rzswitch.exe`, runs with
  `--foreground-tracker`); `CopyTrackerExe` publish target keeps it next
  to `rzswitch.exe` automatically.
