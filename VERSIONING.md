# Versioning rules

Single source of truth for a release is the **git tag**. No hardcoded
`<Version>` in csproj files (avoids drift between code and tag).

## Format

SemVer: `vMAJOR.MINOR.PATCH` (e.g. `v1.2.3`), tag on the release commit.

- **MAJOR** — breaking change: SignalR hub contract
  (`/GameManagerServiceHub`, event shapes), registry keys Synapse reads
  (`ServerPort`), service name `rzswitch`, installer requirements.
- **MINOR** — new feature, backward compatible: new panel button/action,
  new web endpoint, new game-list source.
- **PATCH** — bugfix / UI polish, no behavior change: button rendering,
  alignment, log format, build config, docs.

Pre-release (if needed): `vX.Y.Z-rc.N`.

## Release checklist

1. `dotnet build rzswitch.slnx -c Release` — 0 errors.
2. Publish both projects Release into one clean folder
   (single-file, framework-dependent, needs .NET 10 runtime on target):
   ```bat
   dotnet publish setup/rzswitch-setup.csproj -c Release -o <dir>/
   dotnet publish rzswitch.csproj -c Release -o <dir>/
   ```
   Note: `rztracker.exe` is auto-copied next to `rzswitch.exe` by the
   `CopyTrackerExe` target — the folder must contain all three exes.
   Note: `rzswitch.exe` in `publish/` is locked while the service runs —
   `sc stop rzswitch` (admin) before re-publishing over it.
3. Attach to the GitHub release a single archive
   `rzswitch-vX.Y.Z-win-x64.zip` with all three exes
   (`rzswitch.exe`, `rzswitch-setup.exe`, `rztracker.exe`).
   `web.config` / `*.staticwebassets.*` / `publish/` subfolder / `*.pdb`
   from the output are Web SDK leftovers — do not attach.
4. Tag and publish:
   ```bat
   git tag vX.Y.Z
   git push origin vX.Y.Z
   gh release create vX.Y.Z <assets> --title "vX.Y.Z" --notes "..."
   ```
5. `publish/`, `bin/`, `obj/` are gitignored local folders, never committed.
