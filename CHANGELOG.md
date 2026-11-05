# Changelog

Add notes for the next release under **Unreleased**. The Release workflow turns that section into the
version it publishes, and the notes appear on the GitHub release and in `cultivate update`.

## Unreleased

Cultivate is now a standalone project that anyone on the team can install, and it keeps itself up to date.

- **Updates without reinstalling.** `cultivate update` downloads the latest GitHub release, checks its
  SHA-256, and swaps the installed files in place, rolling back if anything fails. Running saved-file
  workers switch to the new version on their own; active deployments are not interrupted. The terminal UI
  tells you when an update is available, and `cultivate doctor` reports it. `cultivate update --version X.Y.Z`
  installs a specific release, for example to roll back.
- **One-click releases.** Run the **Release** workflow and choose patch, minor, or major. It bumps the
  version, moves these notes into the release, builds and self-tests the package, then tags and publishes it.
- **Own repository and installer.** `scripts/install.ps1` installs to
  `%LOCALAPPDATA%\Programs\Cultivate`, adds it to the user PATH (run `cultivate` from any
  terminal), installs the MSBuild hook, and creates Start menu and desktop shortcuts.
  `uninstall.ps1` reverses it. Release zips install themselves.
- **No dependency on the WebfarmWorkspace utility.** The journal module and MSBuild hook ship with
  Cultivate. The installer migrates the old `Meyer.WebfarmWorkspace.targets` hook. Existing
  settings and journals keep working, and earlier deployments stay restorable.
- **First-run setup** (`cultivate setup`) and a scriptable form with options. The UI's
  **Settings** now covers the checkout, baseline branch, webfarm, mapping assembly, MSBuild, and
  automatic updates.
- **`cultivate doctor`** checks the runtime files, PATH, PowerShell, Git, MSBuild hooks, settings,
  webfarm reachability, the mapping assembly, journal access, and available updates.
- `cultivate config`, `cultivate --version`, and an expanded `--help`.
- New settings: `MappingAssembly` (replaces the legacy utility's `DependencyAssembly`) and `CheckForUpdates`.
- `CULTIVATE_HOME` relocates the data folder.
- Visual Studio Debug builds no longer start PowerShell after every build. Their webfarm copy is
  kept local, as before.
- CI builds, packages, installs, and runs the self-test on every push.

## 1.0.0 — 2026-11-04

First version, installed locally under `%LOCALAPPDATA%\Meyer\Cultivate`.
