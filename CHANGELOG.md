# Changelog

Add notes for the next release under **Unreleased**. The Release workflow turns that section into the
version it publishes, and the notes appear on the GitHub release and in `cultivate update`.

## Unreleased

## 1.1.4 — 2026-11-05

- **Deployment summary.** After an activation completes, Cultivate lists every webfarm file it updated and how:
  built and copied, synced, deleted, or restored to its original.
- In the deployment preview, a project with several changed files shows its build once; later rows say
  "(same build)". Each changed project was already built only once.

## 1.1.3 — 2026-11-05

- Fix: a project whose HintPath names a package version that `packages.config` never restores (MyMeyer's
  `Microsoft.Bcl.AsyncInterfaces.10.0.2`, for example) no longer blocks activation. Like Visual Studio, Cultivate
  now builds against the checkout's `bin` copy, but only when its assembly version exactly matches the version
  the `<Reference>` declares; any other version still blocks.

## 1.1.2 — 2026-11-05

- A new banner on the home, welcome, settings, and field guide screens: the app icon's seedling as pixel art
  beside a big CULTIVATE wordmark that ripens from green to gold. Terminals that are too narrow or short for
  it get the compact header, so the menu never scrolls off screen.

## 1.1.1 — 2026-11-05

- Fix: `cultivate update` no longer reports an error after it has replaced the files. A replaced single-file
  executable cannot load more of its own code, so the verified new version now performs the swap while the
  old one only waits. Updating from 1.1.0 itself may still show that error once; the files are updated anyway.
- Error messages are never blank.

## 1.1.0 — 2026-11-05

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
