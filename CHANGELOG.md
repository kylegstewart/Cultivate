# Changelog

Add notes for the next release under **Unreleased**. The Release workflow turns that section into the
version it publishes, and the notes appear on the GitHub release and in `cultivate update`.

## Unreleased

- Improvement: Esc now goes back one screen, at once, everywhere in Cultivate: every screen, menu, question and list. It never
  needs Enter and never confirms anything: in a menu it does what its Back, Cancel or No does, a yes or no question takes it as No,
  and a list of files or library changes to check goes back with nothing changed. While a build or warm-up runs, Esc still cancels
  or stops waiting. On the home screen Esc moves to Exit instead of closing Cultivate, so pressing it a few times always brings you
  home. Back now returns to the screen you came from: from a deployment preview to the version or pull request list as you left it,
  and from Open solution's repository list to the worktrees and branches. Yes or no questions take one key (Y, N, or Enter for the
  answer shown), choosing a solution no longer needs Enter to leave, and every screen and menu lists its keys on its last line. Home
  keeps the action you last chose highlighted when you come back to it. Typing on the home screen and in the short action menus no
  longer searches them; the longer lists of repositories and checkouts still filter as you type.
- Improvement: **Settings** is now a list you can leave at any time with Esc, instead of fifteen questions in a row. Each setting
  shows its value, and the highlighted one is explained in a card below with any warning about it (a webfarm that cannot be reached,
  a mapping assembly that is not built yet). Enter edits a text setting in place (the arrows, Home and End move in it, Ctrl+U
  clears it, and a pasted path works), and saves it at once; Esc cancels the edit. A value that is not valid keeps the field open
  with the reason under it. Enter or Space turns a yes/no setting on or off. Every rule still applies: changing the MyMeyer checkout
  resets the mapping assembly, changing the webfarm root suggests the site address again, and the webfarm root cannot change while a
  deployment is active. `cultivate setup` opens the same list. The first run still asks one question after another, but Esc now goes
  back to the previous question, Y or N answers a yes/no question in one key, and Esc on the first question leaves setup without
  saving anything.

## 1.3.0 — 2026-11-21

- Feature: build the Meyer Online web release package. The new home action Build MeyerOnline release (and `cultivate online-release`)
  does in your MeyerOnline checkout what you did by hand: it switches the checkout to `master` and pulls it (fast-forward only), runs
  `npm install` when `node_modules` is missing or the pull changed `package.json`, publishes `Meyer.Online.Server` with its
  `FolderProfile` publish profile exactly as Visual Studio does while `npm run build-prod` builds the app, copies the app's `www` folder
  into the publish folder as `wwwroot`, and zips the publish folder into `MORelease\MORelease.zip` in your release folder, replacing the
  old zip only once the new one is complete and checked. Anything Git cannot do exactly stops it before anything else changes: a merge
  or rebase in progress, modified tracked files (unless you confirm, or pass `--allow-local-changes`), local commits on `master`, or
  `master` checked out in another worktree. The preview says what building would do; the result gives the zip's size, files and SHA-256,
  the commit built, what was pulled and switched, and each step's time. The checkout is found beside your MyMeyer checkout, or set with
  `cultivate setup --meyeronline PATH`. It never touches a webfarm or a database.
- Improvement: release files now go in a `Releases` folder in your Documents folder (`Documents\Releases\<ticket>`) instead of
  straight into Documents. Settings that still have the old default, the Documents folder itself, move there on their own; a folder
  you chose stays. The folder is created the first time release files are written, and release folders already in Documents stay
  where they are.
- Improvement: **Open solution** → **Open main worktree solution** now pulls the main worktree's branch before Visual Studio starts,
  so the solution opens on current code. It fetches the branch from origin and fast-forwards it, the same way building release files
  pulls master: local commits, or local changes the pull would overwrite, leave it as it is with a warning. One line says what was
  pulled, or why not (detached HEAD, a branch origin does not have, a failed fetch), and which branch it is on when that is not the
  base branch. The solution opens either way; a warning stays on screen until you press a key. Opening a worktree or a local branch
  pulls nothing.
- Feature: building release files now also builds the pull requests a pull request links. After describing the requested pull request,
  Cultivate reads the **Associated PRs** section of its description and of every linked pull request's own description (each one once),
  and builds each merged one in its local clone beside your MyMeyer checkout, in the same run. Everything goes in the ticket folder, now
  always with one subfolder per repository (for example `Releases\39126\MyMeyer` and `Releases\39126\Public--Applications`). A linked
  pull request that is not merged, is merged into a branch other than its repository's release branch, cannot be read by the GitHub CLI,
  or whose repository is not cloned is listed with the reason and skipped. One repository failing to build no longer stops the others:
  their files are written, the result says which repository failed and why, and `cultivate release --yes` exits 1. `--no-linked` builds
  only the requested pull request. Each repository's release branch is pulled as master is, and `--no-pull` leaves them all.
- Feature: build release files for any cloned repository. `cultivate release --pr` also takes a pull request URL, and `--repo NAME`,
  `--repo OWNER/NAME` or `--repo PATH` picks a clone beside MyMeyer for a number. In the terminal UI, **Build release files** still lists
  MyMeyer's merged pull requests first; Ctrl+R lists another clone's, and a pasted pull request link builds that pull request in its own
  repository. Another repository's releases are built from master, or from origin's default branch when it has no master. Its projects
  need no `CopyBuildOutputToWebfarm` target: each project's own DLL is captured after its Debug build from master's snapshot, and a copy
  of it that the repository tracks in Git is never mistaken for the build's. References are resolved as MSBuild resolves them in your
  checkout: the HintPath inside master's snapshot, then the HintPath from your project folder (which reaches sibling repositories such
  as `..\..\MyMeyer\bin`), then the project's output folder, MSBuild's last stop (master's tracked copy, then yours). Each DLL taken
  from your checkout is listed with its path, version and date. A reference or NuGet package found nowhere is left to MSBuild, as
  Visual Studio does: the build fails only if the code needs it, and one warning lists them (`build.log` says where Cultivate looked).
  Cultivate never restores packages for these repositories. SDK-style projects are not supported and say so, without hiding the rest
  of the preview. When a repository's build fails after its release branch was pulled, the result reports what the pull did.
- Feature: SQL changes are released too. A repository with no project files, such as Database--SQL, is a SQL repository: every `.sql`
  file its pull request changed is copied to the ticket folder's `SQL` subfolder as the release branch has it now (with the usual
  same / changed again / undone / deleted statuses), read straight from the commit with checkout line endings, so no checkout of the
  whole repository is needed and two scripts whose names differ only by case each keep their own content. Such twins cannot share
  the folder: the second goes in a subfolder, with a warning. Nothing is built or run. When two linked pull requests would use the
  same subfolder, the second gets its own (`SQL (Other--SQL)`, `MyMeyer (PR 2824)`) and the preview says why.

## 1.2.1 — 2026-11-20

- Improvement: building release files now also pulls master, so GitHub Desktop no longer shows it behind afterwards. `origin/master`
  moves to the commit the files were built from, and your local `master` fast-forwards to it in whichever checkout or worktree has it
  checked out (or just moves, when none does). Local changes the pull does not need to touch stay as they are. When `master` has
  commits origin does not, or the pull would overwrite files you changed, `master` is left as it is and the result warns with the files
  and what to do; the release files are built from origin's master either way. The preview says how far behind `master` is, the result
  says what was pulled, and `cultivate release --no-pull` leaves `master` and `origin/master` where they are.

## 1.2.0 — 2026-11-19

- Fix: building release files or activating a deployment no longer stops with "Incompatible input DLLs" when two of the projects it
  builds use different copies of the same DLL (for example `BusinessLogic.dll` 6.29.0 from a package and an older build in a sibling
  repository). Each project now compiles against its own references, as in Visual Studio, and can no longer silently pick up another
  project's copy. When a release project's HintPath points outside master (such as a sibling repository's Debug build), it now
  compiles against the copy the projects it depends on use from master, and `build.log` says whose copy that is, instead of your
  local build.
- Improvement: when a project fails to compile, the failure report now says which errors are on lines your version did not
  change ("Not from your changes: ... line 19 is unchanged from master."). When every error is, it no longer tells you to fix them
  here: it says the project probably does not compile on master either and how to deploy without it. In the terminal UI, after one
  project fails to compile and the plan has other work, Cultivate offers (default No) to leave that project's changed code out
  and return to the preview; its DLL on the webfarm stays as it is and its changed frontend files still deploy. `cultivate plan`
  and `activate` take `--exclude PATH` (repeatable) with the same meaning as Choose files to deploy, and the MCP `plan`, `build`
  and `activate` tools take an `exclude` list; MCP `build` errors carry `changed`.

## 1.1.26 — 2026-11-19

- Feature: build a merged pull request's release files. `cultivate release --pr N` fetches the pull request and master from origin
  into private refs (your checkout, its branches and `origin/master` are not touched) and previews, as JSON, what master holds for
  each file the pull request changed: the same, also changed by other merged work, undone, or deleted. With `--yes` it builds the
  projects that own the changed code from master in a throwaway snapshot, Debug like the dev farm, and writes their DLLs, any
  package DLL the pull request bumped, and the changed content files (markup, scripts, styles, images, `.ashx` handlers and the
  like) to one flat folder named after the ticket in your Documents folder, ready to upload to the Code Release Manager. The
  unchanged projects those DLLs compile against are built from the same snapshot first instead of taken from your checkout's
  `bin`, and are not copied. The result lists the other merged commits each DLL carries, references that still came from outside
  the snapshot, and the SQL, config and other files you must release by hand. Files already in the folder are kept and reported;
  `--clean` removes only unchanged files an earlier run wrote. `--name`, `--out`, `--reuse-dependencies` and
  `cultivate setup --release-root` adjust it. The GitHub CLI is required. In the terminal UI, the **Build release files** home
  action lists your recently merged pull requests (or everyone's, or any number you type), shows the preview to check before
  building, builds with live progress (Esc cancels before anything is written), and ends with a link to the folder and a way to
  open it in Explorer. It never touches your dev webfarm, so it works without an active deployment.

## 1.1.25 — 2026-11-19

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
