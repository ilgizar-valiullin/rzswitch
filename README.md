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

## Control panel

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
:: 1. Stop and disable the original (admin)
sc stop "Razer Game Manager Service 3"
sc config "Razer Game Manager Service 3" start= disabled

:: 2. Start Synapse (it applies the profiles)

:: 3. Panel (admin) -> "Install service", then "Enable our service"
rzswitch.exe
:: open http://127.0.0.1:<port> (port is in the log and in registry ServerPort)
```

Rollback: the panel's "Enable GMS" button (`start= demand` + start), or
`sc config` back manually; remove our service with the panel button.

## Build

```
dotnet build -c Release
```

.NET 8+ SDK. Single-file `Program.cs`, no runtime dependencies beyond
ASP.NET Core. Windows-only (window detection, registry, services).

## Status

Working prototype: hub comes up, games load from UserGameList, START/STOP
events go out to subscribers. Verified against a live Synapse — see Issues.

## License

MIT, see `LICENSE`.
