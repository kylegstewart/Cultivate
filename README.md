# Cultivate

A Windows terminal app for MyMeyer developers. Pick a worktree or branch, preview exactly
which files and projects it changes, then deploy it to **your dev webfarm**. It builds only
the projects that own changed code, journals every file it overwrites, and can restore the
originals. It can also keep the webfarm in sync as you save.

```
cultivate
```

<img src="assets/Cultivate.png" width="96" alt="Cultivate icon: a seedling over crop rows and a golden sun">

- **Preview before anything happens.** Enter opens a plan: frontend destinations, projects to
  build, and anything that blocks deployment. Nothing is deployed until you confirm.
- **Changed projects only.** Only projects that own changed compiled files are built (Debug,
  `BuildProjectReferences=false`, inside an isolated snapshot). Unchanged dependencies are never
  rebuilt or copied.
- **Safe to undo.** Originals are backed up and SHA-256 verified. **Restore** puts them back.
  Edits made to the webfarm by anything else block the restore instead of being overwritten.
- **Your checkout is never touched.** No branch switches, commits, pushes, SQL, or site config.

See [docs/how-it-works.md](docs/how-it-works.md) for the full behavior and safety model.

## Requirements

| Requirement | Why |
| --- | --- |
| Windows 10/11 x64 | Cultivate ships as a self-contained `win-x64` executable; no .NET runtime needed. |
| [Git for Windows](https://git-scm.com/download/win) on `PATH` | Worktree discovery, snapshots, and change detection. |
| Visual Studio 2022+ (or Build Tools) with MSBuild and the .NET Framework targeting packs | Builds the changed MyMeyer projects. |
| A normal MyMeyer checkout with `bin\MeyerObjects.dll` built | Code Release Manager mapping lookups for frontend files. |
| Network access to your dev webfarm share and the Meyer database | Deployment destination and file mappings. |
| [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) | **Only** to build Cultivate from source. |

## Install

### From a release (recommended for most people)

1. Download `Cultivate-<version>-win-x64.zip` from this repository's **Releases** page, or run
   `gh release download --repo <owner>/Cultivate --pattern "*.zip"`.
2. Extract it anywhere, then in PowerShell, from the extracted folder:

   ```powershell
   powershell -NoProfile -ExecutionPolicy Bypass -File .\install.ps1
   ```

### From source

```powershell
git clone <repository-url> Cultivate
cd Cultivate
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\install.ps1
```

Either way, the installer:

- copies Cultivate to `%LOCALAPPDATA%\Programs\Cultivate`
- adds that folder to your **user PATH**, so `cultivate` works in any new terminal
- installs Cultivate's per-user MSBuild hook (see [How builds are protected](docs/how-it-works.md#how-visual-studio-builds-are-protected))
  and replaces the older WebfarmWorkspace hook if you had it
- creates **Cultivate** shortcuts in the Start menu and on the desktop

Installer options: `-InstallDir <path>`, `-NoPath`, `-NoDesktopShortcut`, `-Package <zip>`, and
`-Force`, which stops a running saved-file worker so the upgrade can replace `Cultivate.exe`.

**After installing:** open a **new** terminal (existing ones keep the old PATH), reload any open
Visual Studio solutions, then run:

```powershell
cultivate doctor   # checks git, MSBuild, hooks, settings, webfarm, and journal access
cultivate          # first run walks you through setup
```

## Use

```
cultivate                                         Interactive terminal UI
cultivate list                                    Worktrees and branches (JSON)
cultivate status                                  Active deployment (JSON)
cultivate plan --worktree "C:\path\to\worktree"   Preview a plan (JSON); exit code 2 if blocked
cultivate plan --branch 38215
cultivate activate --worktree "C:\path" --yes     Build, deploy, keep syncing saved changes
cultivate activate --branch origin/feature --yes --once
cultivate restore --yes                           Stop syncing and restore original webfarm files
cultivate setup                                   Change settings (interactive or with options)
cultivate config                                  Show the settings file and values
cultivate doctor                                  Check the installation
cultivate self-test                               Fixture-only tests; never touches a real webfarm
cultivate --version | --help
```

Commands that change the webfarm require `--yes` when run non-interactively.

In the UI: **Tab / Left / Right** switch between Worktrees, Local and Remote branches. Type to
search, use the **arrow keys**, **PgUp/PgDn** and **Home/End** to move, **Ctrl+U** clears the
search, **Enter** opens a preview, and **Esc** goes back.

## Configuration

Settings live in `%LOCALAPPDATA%\Meyer\Cultivate\settings.json`. Change them with `cultivate setup`
or from **Settings** in the UI. To script it:

```powershell
cultivate setup --repository "$HOME\Documents\GitHub\MyMeyer" --webfarm "\\webfarm-dev02\C$\inetpub\MyMeyer"
```

| Setting | Default | Meaning |
| --- | --- | --- |
| `RepositoryRoot` | `~\Documents\GitHub\MyMeyer` if present | Your normal MyMeyer checkout (contains `MyMeyer.sln`). Worktrees are discovered from it. |
| `BaseBranch` | `master` | Changes are measured from the merge-base with this branch. |
| `WebfarmRoot` | your `WebfarmPath` user environment variable | Dev site root, e.g. `\\webfarm-dev02\C$\inetpub\MyMeyer`. Use one canonical UNC path. |
| `MSBuild` | latest Visual Studio, found with `vswhere` | MSBuild used for project builds. |
| `MappingAssembly` | `<RepositoryRoot>\bin\MeyerObjects.dll` | Provides the Code Release Manager database connection. |
| `WatchSavedFiles` | `true` | Whether activation keeps syncing later saves by default. |
| `StateRoot` | `%LOCALAPPDATA%\Meyer\WebfarmWorkspace` if it exists, else `...\Meyer\Cultivate\State` | Deployment journal, backups, and locks. **Never delete while a deployment needs restoring.** |

Set the `CULTIVATE_HOME` environment variable to move the whole data folder (useful for CI and testing).

## Where things live

| Path | Contents |
| --- | --- |
| `%LOCALAPPDATA%\Programs\Cultivate\` | Program files: `Cultivate.exe`, PowerShell bridge and journal module, MSBuild targets, `uninstall.ps1`, `installation.json` |
| `%LOCALAPPDATA%\Meyer\Cultivate\` | Your data: `settings.json`, `Logs\`, `Runs\<run-id>\` (plans, build logs, binlogs), `Workers\` |
| `StateRoot\<destination-hash>\` | The deployment journal and backups for each webfarm |
| `%LOCALAPPDATA%\Microsoft\MSBuild\{Current,15.0}\Microsoft.Common.targets\ImportAfter\Meyer.Cultivate.targets` | Per-user MSBuild hook |

Logs contain local paths and build diagnostics. Don't share them publicly.

## Upgrade and uninstall

- **Upgrade:** re-run `install.ps1` from a newer release or an updated clone. Settings, logs, and
  the journal are kept. If a saved-file worker is running, close it or pass `-Force`. Stopping it
  leaves the current deployment in place; activate again to resume automatic updates.
- **Uninstall:** run `cultivate restore --yes`, then
  `& "$env:LOCALAPPDATA\Programs\Cultivate\uninstall.ps1"`. This removes the program files, PATH
  entry, MSBuild hook, and shortcuts. Your data folder and journal are kept.

### Coming from the earlier local install

Earlier builds lived in `%LOCALAPPDATA%\Meyer\Cultivate\App` and depended on the separate
WebfarmWorkspace utility in `%LOCALAPPDATA%\Meyer\WebfarmWorkspace\App`. Version 1.1 bundles
everything it needs. Running `install.ps1`:

- moves the MSBuild hook from WebfarmWorkspace to Cultivate (the old hook file is backed up to
  `%LOCALAPPDATA%\Meyer\Cultivate\Migrated`)
- retargets the existing desktop shortcuts
- keeps your existing `settings.json`, logs, and journal, so earlier deployments stay restorable

The old `App` folder, the source copy in `%LOCALAPPDATA%\Meyer\Cultivate\src`, and the
`WebfarmWorkspace\App` folder are no longer used. Delete them once you've confirmed the new install
works. Keep `WebfarmWorkspace\<hash>` folders while they hold a deployment journal.

## Development

```powershell
dotnet build src\Cultivate                      # compile (warnings are errors)
dotnet run --project src\Cultivate -- doctor    # run any command from source
.\scripts\build.ps1 -Package                    # self-contained publish + release zip in artifacts\
.\scripts\install.ps1                           # build and install your working copy
cultivate self-test                             # end-to-end fixture tests (needs the MSBuild hook installed)
```

| Path | Purpose |
| --- | --- |
| `src/Cultivate/*.cs` | The app: `Program` (commands), `Terminal`/`VersionPicker`/`ActionPicker` (UI), `Planner` and `GitRepository` (what changed and who owns it), `Pipeline` (snapshot, build, verify, deploy), `Watcher` (saved-file worker), `Bridge` (calls the journal), `Setup`, `Doctor`, `SelfTest` |
| `src/Cultivate/Runtime/` | Files that ship beside the exe: `Bridge.ps1`, `Cultivate.Journal.psm1` (lock, journal, backup, verified copy, restore), `Cultivate.Build.targets` (MSBuild hook), `Capture.targets` (captures a project's DLL during Cultivate builds) |
| `assets/` | Icon sources. `assets\Build-Icon.ps1` regenerates the PNG and ICO from the SVG. |
| `scripts/` | `build.ps1`, `install.ps1`, `uninstall.ps1` |
| `.github/workflows/` | CI (build, package, install, self-test) and tag-triggered releases |

`self-test` builds tiny disposable .NET Framework 4.7.1 projects, deploys them to a temporary
folder, and checks planning, builds, restores, the journal, and the background worker. It never
touches your settings or a real webfarm.

### Releasing

1. Update `<Version>` in `src/Cultivate/Cultivate.csproj` and add an entry to `CHANGELOG.md`.
2. Merge to `main`, then tag and push: `git tag v1.2.0 && git push origin v1.2.0`.
3. The **Release** workflow checks that the tag matches the version, runs the self-test, and
   publishes `Cultivate-<version>-win-x64.zip` to GitHub Releases. Users upgrade by installing it.
