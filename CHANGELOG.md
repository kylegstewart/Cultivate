# Changelog

## 1.1.0 — 2026-11-05

Cultivate is now a standalone project that anyone on the team can install.

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
  webfarm reachability, the mapping assembly, and journal access.
- `cultivate config`, `cultivate --version`, and an expanded `--help`.
- New `MappingAssembly` setting. It replaces the legacy utility's `DependencyAssembly`.
- `CULTIVATE_HOME` relocates the data folder.
- Visual Studio Debug builds no longer start PowerShell after every build. Their webfarm copy is
  kept local, as before.
- CI builds, packages, installs, and runs the self-test. Pushing a `v*` tag publishes a release.

## 1.0.0 — 2026-11-04

First version, installed locally under `%LOCALAPPDATA%\Meyer\Cultivate`.
