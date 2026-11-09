# Changelog

Add notes for the next release under **Unreleased**. The Release workflow turns that section into the
version it publishes, and the notes appear on the GitHub release and in `cultivate update`.

## Unreleased

- Feature: deployment previews offer Choose files to deploy. Uncheck changed files to exclude them
  from activation and automatic updates, with exclusions shown in the preview and preserved for the
  active source. Excluded compiled files use baseline contents in the isolated build snapshot so
  their changes cannot enter a DLL built for another selected file in the same project.

## 1.1.16 — 2026-11-06

- Change: activation previews default to Activate once (no automatic updates).

## 1.1.15 — 2026-11-06

- Feature: failures now identify the operation, project, branch/worktree, exit code, and useful error lines,
  with targeted checks for compiler, reference, package, targeting-pack, and file-access problems.
  Build and restore logs include phase/project labels, command context, and binary-log paths. The terminal,
  CLI, and automatic-update status share the diagnosis; dated incident reports preserve the full exception
  alongside last-error.log, and logging failures do not hide the original error.

## 1.1.14 — 2026-11-06

- Feature: Open solution also lists worktrees from any repository under a default worktrees folder
  (`WorktreesRoot`, set with `cultivate setup` or `--worktrees PATH`; defaults to `Documents\GitHub\t3-worktrees`).
  Each opens its solution, asking which one when there are several, plus the changed source files under it.
- Change: the deploy confirmation now defaults to yes.
- Fix: a project reference whose DLL can't be found, such as a `HintPath` to a package that
  `packages.config` never restores, no longer stops the build. Like Visual Studio, MSBuild warns and the
  build fails only if the code uses it. A DLL with a different version is still never substituted.

## 1.1.13 — 2026-11-06

- Fix: a webfarm file changed outside Cultivate, such as by a manual Visual Studio build, no longer blocks
  activating or restoring a deployment. Cultivate leaves that file as is instead of restoring over it, stops
  tracking it, and lists it as "Kept (changed outside Cultivate)". If the new deployment includes the file,
  Cultivate replaces it and backs up the manual build as its original.

## 1.1.12 — 2026-11-06

- Fix: Backspace, Delete, and other editor commands work in documents Cultivate opens. Visual Studio now
  starts directly with the solution instead of showing the Start window, which stayed open behind the IDE
  and blocked those commands.

## 1.1.11 — 2026-11-05

- Fix: opening a solution in a new Visual Studio instance closes the tabs Visual Studio restores from the
  previous session, such as DLLs and duplicate views, so only the solution's changed files are open.
  Tabs in a Visual Studio instance that already has the solution open are left alone.

## 1.1.10 — 2026-11-05

- Fix: opening a solution waits and retries when Visual Studio is busy resolving automation members
  such as the open-document count, including dispatch errors reported by the .NET dynamic binder.
  Genuine missing-member errors still fail immediately, and retries preserve existing document tabs.

## 1.1.9 — 2026-11-05

- Fix: installing Cultivate no longer redirects or blocks ordinary Visual Studio and MSBuild webfarm
  copies. Only Cultivate's own builds use private staging, with validation before their copy target runs.
  Missing runtime files do not break normal builds. Manual webfarm changes and damaged backups are
  detected before Cultivate prepares a deployment and rechecked before it changes the active journal.
  Status reads share the journal lock with worker updates to avoid intermittent file access failures.

## 1.1.8 — 2026-11-05

- Fix: opening a solution opens only actionable changed source and markup files. Binaries, generated files,
  project metadata, and other non-code changes are skipped. Reopening reuses the matching solution and
  its existing documents instead of opening duplicate tabs.

## 1.1.7 — 2026-11-05

- Fix: opening a solution finds Visual Studio when a newer SQL Server Management Studio or Build Tools
  installation is also present. Preview editions are included in the IDE search.

## 1.1.6 — 2026-11-05

- **Open changed files.** Opening a solution also opens the branch's changed files in the same Visual Studio
  instance, including saved worktree edits and untracked files. Deleted files are skipped.

## 1.1.5 — 2026-11-05

- **Open solution.** The homepage can open a worktree or local branch's MyMeyer solution in its associated
  application. Branches reuse their existing checkout or get a new worktree without switching your current branch.

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
