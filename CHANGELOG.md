# Changelog

Add notes for the next release under **Unreleased**. The Release workflow turns that section into the
version it publishes, and the notes appear on the GitHub release and in `cultivate update`.

## Unreleased

- Improvement: the dev site address in the warm-up result is a link; Ctrl+click it in Windows Terminal to open the site.
- Improvement: the terminal UI shows an activation's build and deployment stages on the same screen as the site warm-up
  that follows. The build and deployment run as before, and Esc never cancels them.
- Improvement: Tidy worktrees responds to keys at once. Moving the highlight, paging, selecting, typing a search,
  sorting and filtering now redraw only the lines that changed, in a single write, instead of the whole screen; a key
  is picked up the moment it is pressed instead of on the next 20 ms check; and a held arrow key's repeats are handled
  together, so the list stops where you let go.
- Improvement: the other screens respond as quickly. The home menu, every action menu, the version and solution
  pickers and the Ticket workspaces list redraw only the lines a key changes, in one write, and a held arrow stops
  where you let go. The home screen appears at once, filling in the active deployment when its status arrives;
  nothing that acts on the deployment starts before it has. Update my webfarm, Open solution and Ticket workspaces
  start fewer Git processes before they open, and screens write to the terminal in one piece instead of many small
  ones.
- Feature: Cultivate cleans up after itself. Once a day, in the background of the terminal UI and the saved-file worker,
  it removes build and plan runs older than 7 days, bridge logs and incident reports older than 14 days, and leftovers
  older than a day (stale worker settings, interrupted requests, self-tests and updates), and has Git forget snapshot
  worktrees whose run is gone. The deployment journal and its backups, the active deployment's run and worker, and
  anything a recent error report names are always kept, and a run another program is using is left in place. The new
  Clean up home action shows what would go and removes it, and separately lists the worktrees unused for 14 days
  (`CleanupDays`; `cultivate setup --cleanup-days`) that lose nothing when deleted, deleting them only after you
  confirm; branches are kept. `cultivate cleanup` prints the same as JSON without changing anything; `--yes` removes
  Cultivate's files, `--worktrees --yes` also deletes those worktrees, and `--days N` sets the age. The self-test now
  keeps its runs, logs and worker files in its own fixture folder instead of Cultivate's data folder.
- Feature: review a pull request on your dev webfarm without creating a branch or worktree. Choose Review a pull request
  on the home screen to pick one of MyMeyer's open pull requests (or type any number, including a closed or merged one),
  or run `cultivate plan --pr N` and `cultivate activate --pr N --yes`. Each preview fetches the pull request's current
  head from origin into a private ref and compares it with the branch it targets; the preview, `status`, and the home
  screen name it as "PR #N: title". The list and the title, author, and base branch come from the GitHub CLI (`gh`);
  without it a pull request can still be reviewed by number, compared with your base branch. Restore removes the
  fetched refs.
- Feature: `cultivate mcp` serves Cultivate to coding agents (Claude Code, Codex, T3 Code) as a Model Context Protocol
  server over stdio. Agents can list worktrees and ticket workspaces, preview a plan, build only the changed projects
  without deploying (compile errors come back with file, line, code and project), activate, request the site, check
  status and restore, with progress for long steps. Activate and restore need `confirm: true`, an activation is
  one-time unless automatic updates are asked for, and only one runs at a time. `cultivate mcp --read-only` leaves out
  activate and restore so an agent cannot change the webfarm.

## 1.1.24 — 2026-11-17

- Feature: activation offers to stop the old WebfarmFileSync watcher instead of failing when it is running. Choose to
  stop it once, to always stop it without asking (`StopLegacyWatcher`; `cultivate setup --stop-legacy-watcher true`),
  or to cancel. With the setting on, automatic updates stop it too. `cultivate activate --stop-legacy-watcher` stops it
  for one activation. A watcher started again during an activation still blocks the publish.
- Feature: a ticket's library changes can be tested before a package is published. When the MyMeyer worktree you
  preview has a worktree on the same ticket (or the same branch name) in a library repository such as
  Libraries--BusinessLogic, the preview lists it. Choose library changes to build its changed projects from a snapshot,
  compile the changed MyMeyer projects against that build, and deploy the library DLL with them. It is left out unless
  you include it, it only replaces a DLL the webfarm already has, nothing is packed or published, and Restore returns
  the original DLL. Automatic updates rebuild when a library file is saved. Library repositories are found beside the
  MyMeyer checkout (`LibraryRepositories`; `cultivate setup --library-repositories`), and `cultivate plan` and
  `activate` take `--library PATH` or `--libraries`.

## 1.1.23 — 2026-11-17

- Improvement: Tidy worktrees opens almost at once after the first visit. The first visit scans every worktree's size and
  state behind a loading screen and saves the results; later visits read them back and only scan worktrees that are new
  or whose HEAD has moved. F5 scans every worktree again, and the highlighted worktree shows when it was last scanned.
  Deleting still re-reads each selected worktree's state before anything is removed.

## 1.1.22 — 2026-11-17

- Feature: after a deployment, Cultivate requests the dev site and waits for it to answer, so the recompilation no longer
  lands on your first page load. A progress bar shows the wait (Esc stops waiting), and the result reports the status or the
  compile, parser, or server error with its file and line. The site address is read from the webfarm's IIS bindings
  (`SiteUrl`; change it with `cultivate setup --site-url`, or turn the warm-up off with `--warmup false`).
  `cultivate warmup` runs the same check on demand, and `cultivate activate --no-warmup` skips it.
- Feature: Ticket workspaces on the home screen groups every repository's worktrees by ticket number (or by branch name
  for work without a ticket). A workspace shows each checkout's uncommitted and unpushed work, its pull request and
  checks (through the GitHub CLI), and whether the ticket's SQL files folder matches the SQL repository's changes. From
  there you can open all its solutions, preview the webfarm deployment, and open the ticket page, pull requests, or SQL
  files folder. `cultivate tickets [--ticket KEY] [--prs]` prints the same as JSON.
- Feature: Tidy worktrees on the home screen lists every repository's worktrees with their size, age, and state. Sort by
  size, age, or name (Tab), filter by text, age (F2), size (F3), or safe to delete (F4), select with Space or Ctrl+A,
  and delete with Del. Deleting asks first, asks again before losing uncommitted work, never touches a main checkout,
  the deployed worktree, or a locked worktree, and always keeps branches. `cultivate worktrees [--sizes]` prints the
  list as JSON.

## 1.1.21 — 2026-11-17

- Fix: Open solution prepares a worktree's dependencies before Visual Studio loads it, so projects no longer open with
  unresolved references. Files Git ignores, such as `packages\` and `bin\MeyerObjects.dll`, are copied from the
  repository's main checkout, and NuGet packages the main checkout lacks too are restored. Nothing that would show in
  `git status` is created, and existing files are never overwritten.

## 1.1.20 — 2026-11-13

- Feature: a changed project file that adds or bumps a NuGet package reference now deploys that package's DLL
  with the project. For example, when MeyerObjects moves to a new BusinessLogic version, `bin\BusinessLogic.dll`
  is copied to the webfarm too. The preview lists these packages under their projects. The DLL must be the
  version the reference declares, and changed projects that need different copies of one DLL block activation.
  Package references a project file didn't change are still never copied, so the shared `bin` isn't downgraded.
- Fix: Open solution opens every changed frontend file, such as an `.ascx` that has no Code Release Manager
  mapping yet, along with its code-behind. Designer files still stay closed. Opening a solution no longer looks
  up webfarm mappings.

## 1.1.19 — 2026-11-12

Maintenance release.

## 1.1.18 — 2026-11-11

Maintenance release.

## 1.1.17 — 2026-11-09

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
