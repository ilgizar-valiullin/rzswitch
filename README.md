# rzswitch

Your Razer profiles switch automatically when you switch games — without Razer's heavy background service running.

Razer Synapse changes profiles per game, but its stock detector is heavy, noisy, and sometimes misses games launched via launchers. rzswitch is a tiny replacement: it watches which window is in front and tells Synapse to switch the profile. Same result, fraction of the footprint.

## What you need

- Windows 10/11 (64-bit)
- Razer Synapse installed
- .NET 10 runtime (the app will tell you if it's missing)
- Administrator rights for the 2-minute setup

## Install

1. Download `rzswitch-vX.Y.Z-win-x64.zip` from the latest release and unpack it anywhere.
2. Run `rzswitch-setup.exe` as administrator.
3. Press **Install to system** — files go to your AppData, rzswitch appears in Windows Programs, the service starts.

Done. Switch between games and watch Synapse follow.

## The setup window

- **Game Manager** — turns Razer's own detector off and on. Ours can't run alongside it, so keep Razer's off.
- **Rzswitch, runs from current folder** — quick mode: the service works right from the folder you unpacked. Good for trying things out.
- **System, copies files to AppData** — the real install: files live in AppData, with an entry in Programs. Survives folder moves and cleanups.

## Uninstall

Open Windows Settings → Apps → find **rzswitch** → Uninstall. The setup opens in removal mode: press **Remove from system**, confirm **"Uninstall complete"**, done.

## If something doesn't work

- **Buttons do nothing / access errors** — run the setup as administrator.
- **Profiles don't switch** — make sure Razer's Game Manager is off and our service is running (the setup window says so).
- **Razer turned its service back on** (e.g. after an update) — rzswitch notices within 30 seconds, turns it back off, and leaves a note in its log.

## For developers

Build: `dotnet build rzswitch.slnx -c Release`. Release rules: see `VERSIONING.md`. Changes: see `CHANGELOG.md`.

## License

MIT, see `LICENSE`.
