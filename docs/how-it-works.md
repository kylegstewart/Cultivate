# How Cultivate works

For the implementation details, see [How Cultivate updates the webfarm](webfarm-pipeline.md): Git snapshots,
Code Release Manager file mappings, MSBuild interception and DLL capture, saved-file updates, and recovery.

## The workflow

The version library separates **Worktrees**, **Local**, and **Remote** sources. The home screen,
version library, and deployment preview always distinguish the **active deployment** from the
version you are **previewing**. Highlight a menu action to see its explanation below the choices.
Typing filters actions, the arrow keys move the highlight, and Enter selects.

1. **Choose a version.** Pick an existing worktree, which includes saved uncommitted changes, or a
   local or cached remote branch, which uses committed contents only. Remote branches are cached
   refs and are not fetched. Nothing is checked out or switched.
2. **Review the preview.** It lists actionable changed files, exact frontend destinations, and the
   projects that own changed compiled files. Files with no deployment action, such as generated
   DLLs, stay hidden unless you choose **Show every changed file**. Use **Choose files to deploy**
   to uncheck files you want to leave out (Space toggles, Enter applies). The preview marks them
   **Excluded** and recalculates the frontend copies and DLL builds. Reopen the checklist to include
   a file again.
3. **Confirm the destination.** Cultivate takes an isolated snapshot, prepares references, builds
   only the changed projects, then syncs the mapped frontend files, each changed project's
   own DLL, and the DLL of any package whose reference a changed project file added or bumped.
4. **Choose automatic updates or a one-time activation.** Automatic updates keep running after
   the menu closes. Frontend saves copy without a rebuild, and compiled changes rebuild only
   their owning projects. A failed build leaves the last successful deployment in place.
5. **Restore original webfarm files** stops updates and restores the journaled originals.

Exclusions apply to both activation and later automatic updates. Reopening the same active worktree
or branch keeps its exclusions; a new activation replaces that selection. Newly changed files are
included unless their paths were excluded. Excluded frontend files are neither copied nor deleted.
When a selected compiled file shares a project with an excluded file, the temporary build uses the
excluded file's baseline contents. Added excluded files are omitted; deleted excluded files are
restored in the snapshot. Your source files stay untouched. Keep related changes selected together:
if selected code requires an excluded change, the build can fail and the current deployment stays
in place.

If a previous Cultivate activation already deployed a file you now exclude, the normal version-switch
cleanup restores its journaled original, subject to the existing protection for external changes.

Only files saved to disk are seen; Cultivate cannot read unsaved editor buffers. It does not
commit, push, run SQL, deploy site configuration, or modify project source. Frontend mappings are
added through Code Release Manager. These all produce explicit errors instead of being guessed:
unmapped frontend files, unowned compiled files, ambiguous destinations, missing build inputs,
conflicting output destinations, and changed server files.

## What counts as a change

The baseline is the selected commit's merge-base with `BaseBranch` (normally `master`), plus
staged, unstaged, and untracked (not ignored) worktree files. Merge-conflicted worktrees are
blocked. Deployments of the baseline branch itself are refused.

**Frontend** extensions: `.ascx`, `.aspx`, `.css`, `.js`, `.html`, `.htm`, common images
(`.png`, `.jpg`, `.jpeg`, `.gif`, `.webp`, `.svg`, `.ico`), and fonts (`.woff`, `.woff2`, `.ttf`,
`.eot`). Each selected file needs an unambiguous mapping in Code Release Manager
(`MEYER..MY_CodeRelease_WebfarmFiles`). `bin`, `obj`, `packages`, `.git`, and `Backup` folders are
excluded.

**Compiled** inputs are `.cs`, `.resx`, `.csproj`, `.props`, `.targets`, and `packages.config`.
They identify their owning projects, and only those owners are built. If two changed projects
reference each other, the references decide the build order. Unchanged dependencies or consumers
are never added. A changed build file that no project owns needs clarification; it never triggers
a site-wide build. Solution files, tracked generated DLLs, documentation, SQL, and site
configuration are shown but not copied.

For example, changes to `MiscApps\MiscApps\PriceGroupMatrix.ascx` and its code-behind produce
exactly these actions:

- Sync the markup to `DesktopModules\PriceGroupMatrix\PriceGroupMatrix.ascx`.
- Build `MiscApps\MiscApps\MiscApps.csproj` in Debug and publish `bin\MiscApps.dll`.

`MeyerObjects`, other consumers, and third-party dependency DLLs are not built or published. The
existing compiled references are used to compile the changed project. Cultivate does not audit or
reconcile the host's entire runtime dependency graph. A markup-only activation builds and copies
no DLLs.

### Changed package references

A changed project DLL can need a package version the webfarm doesn't have yet. For example, when
a branch bumps `BusinessLogic` in `MeyerObjects\MeyerObjects.csproj` (and its `packages.config`),
the preview shows:

- Build `MeyerObjects\MeyerObjects.csproj` and publish `bin\MeyerObjects.dll`.
- Publish `bin\BusinessLogic.dll`, the new package version that build copied beside it.

For each changed project file, Cultivate compares its `packages\` `HintPath` references with the
baseline version of that file. A reference that is new, or whose `HintPath` or declared `Version`
changed, publishes its DLL with the project. In a brand-new project file, every package reference
counts as new. A package reference the project file did not change is assumed to be on the webfarm
already and is never republished, so other DLLs in the host's shared `bin` aren't downgraded.
Removed references delete nothing.

The published file is the one the build copied to the project's output folder. Its assembly name
must match, and its version must equal the reference's declared `Version`. Otherwise the deployment
stops. If two changed projects need different copies of the same DLL, the preview blocks activation
until they reference the same package version. Strong-named packages must still match the site's
`web.config` binding redirects, because Cultivate doesn't deploy site configuration.

## Builds

Builds use Visual Studio MSBuild in Debug, with `BuildProjectReferences=false`, isolated writable
outputs, and each project's existing configuration. There is no solution build.

- Package directories are copied from the selected worktree or the normal checkout when available.
  Missing packages for a changed project trigger a project-level NuGet restore, using the existing
  feeds and their local authentication. The restore uses the snapshot as `SolutionDir`, so legacy
  `packages.config` projects restore into the snapshot's own `packages` folder.
- After the restore, literal package `HintPath`s are used directly when they exist. If one is still
  missing, Cultivate accepts a copy from the selected worktree's or normal checkout's `bin` only
  when its assembly version exactly matches the reference's declared `Version`. Otherwise the
  reference is left unresolved, as in Visual Studio: MSBuild warns and the build fails only if the
  code uses it. Cultivate never substitutes a DLL with a different version. See [reference preparation](webfarm-pipeline.md#4a-preparing-references-pipelineprepare).
- Literal `HintPath`s are tried in the snapshot, the selected worktree, and the normal checkout
  before generic `bin` caches. This keeps sibling-repository references (such as
  `Warehouse--PickTickets`) working even when a worktree is relocated. Each seeded reference's
  source is recorded in `build.log`.
- Existing compiled references are copied in privately when needed. Unchanged reference projects
  are not restored or built. If their DLL is missing, Cultivate reports it rather than silently
  rebuilding.
- Build inputs never enter the deployment payload, except for [changed package references](#changed-package-references).
  The webfarm must already contain every other API the newly built project uses.

Each project's existing `CopyBuildOutputToWebfarm` target still runs, but Cultivate's MSBuild hook
redirects its copy to a private local folder. `Capture.targets` records the project's own DLL,
which is checked for the expected assembly identity and published through the journal.
Other copy-local dependencies and whole `bin` folders are never published. A frontend-only saved
update reuses the active project and package DLLs without rebuilding. Project-specific custom
external deployment tasks are not supported.

Build and restore logs, per-project MSBuild binary logs, and saved plans are kept in
`%LOCALAPPDATA%\Meyer\Cultivate\Runs\<run-id>`. Temporary Git worktrees are removed after each run.

## How Visual Studio builds are protected

`install.ps1` adds one per-user MSBuild import, in two places:

```
%LOCALAPPDATA%\Microsoft\MSBuild\Current\Microsoft.Common.targets\ImportAfter\Meyer.Cultivate.targets
%LOCALAPPDATA%\Microsoft\MSBuild\15.0\Microsoft.Common.targets\ImportAfter\Meyer.Cultivate.targets
```

It affects only **Debug** builds of projects under a `MyMeyer.sln` that define
`CopyBuildOutputToWebfarm`. Before that target runs, its destination is redirected to a local
staging folder, which is deleted afterwards. As a result, a normal Visual Studio build from **any**
worktree can't overwrite the version Cultivate deployed. To put a build on the webfarm, deploy it
with Cultivate. Release builds are unaffected. No project or system-wide MSBuild files are
modified.

`WebfarmPath` must be an environment variable, not a global `/p:WebfarmPath` property. Global
overrides are rejected rather than allowed to bypass the redirect. Reload Visual Studio
solutions after installing or uninstalling so they pick up the change.

## Visual Studio reference warnings

Cultivate runs MSBuild independently of Visual Studio. You don't need to open a reference's
Properties or build the solution before activating. Warning icons on first load don't show
whether a reference is really missing or whether the IDE simply hasn't refreshed its design-time
state.

Keep each package's `packages.config` entry, assembly identity, and `HintPath` consistent.
Restore downloads declared packages; it doesn't repair incorrect references. To repair a
reference, reinstall the declared package through NuGet for the affected project. An intentional
version upgrade must update the manifest and references together. Don't compensate with a global
`ReferencePath` or a Visual Studio-installed DLL; that can hide incorrect inputs on one machine.

## Safety and recovery

- Each webfarm destination has an exclusive lock, a journal, SHA-256 verified copies, and backups of
  every file before it is overwritten or deleted. Existing backups are never discarded.
- Builds and verification finish before the live version is switched. If a deployment copy fails,
  newly copied files are restored where possible, and the journal is kept if recovery is blocked.
- A webfarm file changed by anything other than Cultivate blocks restoring that file. It is never
  overwritten.
- Cultivate refuses to activate while the old `WebfarmFileSync` watcher is running against the
  configured farm. Close that watcher first.
- A multi-file SMB/IIS deployment is **not atomic**. Requests can see intermediate files, and DLL
  copies can recycle IIS. Use a dev farm, never production.
- After an interruption or failure, check `cultivate status` and the logs, then use Restore.
  Don't delete the `StateRoot` folders.
- Always use the same canonical UNC path. Drive aliases or alternate shares don't share
  destination ownership. This is per-user, per-machine protection, not a shared deployment
  service.

The journal format is shared with the original WebfarmWorkspace utility, so `cultivate restore`
also restores deployments that utility made. Don't run two different publishing tools against one
destination.
