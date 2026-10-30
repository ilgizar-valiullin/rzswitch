# rzswitch

Lightweight replacement for Razer GameManagerService3. Watches the
foreground window and notifies Synapse via the same SignalR hub contract
(`/GameManagerServiceHub`), so Synapse auto-switches profiles — without the
stock detector service (138+ MB RAM, 25 MB of logs, missed detections on
launcher/child processes).

## How it works

1. Stock GameManager hosts Kestrel on `127.0.0.1:<dynamic port>`, writes the
   port to `HKLM\Software\Razer\Services\GMS3\ServerPort`, and serves the
   SignalR hub `/GameManagerServiceHub`.
2. Synapse (RazerAppEngine) reads the port from the registry, connects, and
   receives `OnGameStartedWithPidEvent({pid, signature})` /
   `OnGameStoppedWithPidEvent(...)`, then applies the linked profile.
3. `rzswitch` does the same: reads the port from the registry (or listens on
   a dynamic port and writes it back to the registry itself, like the
   original), loads the game list from the shared `UserGameList_*.xml` files
   (the same files whose paths are published in the registry as
   `UserGameListPath`, `LocalGameListPath`, `GameSettingsListPath`), and
   pushes events on window focus — only when a game is actually foreground.

The stock detector skips games launched via launchers
(`Is LaunchHelper / Is child process, so not notifying GameStarted` — visible
in its own logs) and fires noise on every Edge renderer process. Here: exact
exe list from your UserGameList + switching only on the active window.

## Installer panel (`setup/`)

`rzswitch-setup.exe` — borderless WinForms installer panel, Razer-styled
(black + neon green, custom title bar, no status line — state is conveyed
by button availability plus a hint line). Runs elevated (manifest requests
administrator).

- Buttons: disable/enable GameManager, **Install & Start our service**
  (single button: installs if missing, then starts), stop/remove it.
- Interlock: start is performed only while GameManager is disabled.
  If GameManager is active, the panel installs (if needed) and tells you
  to disable GameManager first.
- sc.exe output log at the bottom, live refresh.

Build (solution `rzswitch.slnx` holds both projects).
Clean + publish everything into one folder so the installer finds
the service binary next to itself:

```bat
dotnet publish setup/rzswitch-setup.csproj -c Release -o publish/
dotnet publish rzswitch.csproj -c Release -o publish/
```

Note: `publish/rzswitch.exe` is locked while the `rzswitch` service
is running — `sc stop rzswitch` (admin) before re-publishing it.

## Control panel (web)

On startup (console or service) a Razer-styled web panel is served:

- status of GameManager (Razer) and rzswitch: Running/Stopped, start type;
- buttons: disable/enable GameManager, install/remove/start/stop the
  rzswitch service;
- interlock: the enable-ours button is unavailable while GameManager is
  active (port/registry conflict) — enforced both in the UI and server-side;
- live log tail with auto-refresh every 2 seconds.

Actions shell out to `sc.exe` and write to HKLM, so run elevated —
otherwise buttons report access errors. A banner in the panel warns when
running non-elevated. No external assets: single self-contained page,
works offline.

## Install

```bat
:: 1. Stop and disable the original (admin) — or use the installer panel
sc stop "Razer Game Manager Service 3"
sc config "Razer Game Manager Service 3" start= disabled

:: 2. Start Synapse (it applies the profiles)

:: 3a. Installer panel (admin): publish\rzswitch-setup.exe
::     -> "Install & Start our service"
:: 3b. Or web panel: run publish\rzswitch.exe elevated, open the URL it logs
```

Rollback: the panel's "Enable GMS" button (`start= demand` + start), or
`sc config` back manually; remove our service with the panel button.

## Build

```
dotnet build rzswitch.slnx -c Release
```

.NET 10 SDK. `rzswitch.csproj` = service, `setup/` = installer panel
(`rzswitch-setup.exe`). Windows-only (window detection, registry, services).

## Status

Working prototype: hub comes up, games load from UserGameList, START/STOP
events go out to subscribers. Verified against a live Synapse — see Issues.

## License

MIT, see `LICENSE`.
