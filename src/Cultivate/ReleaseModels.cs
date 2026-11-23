namespace Cultivate;

/// <summary>What <c>cultivate release</c> was asked for beyond the pull request number.</summary>
/// <param name="Name">Folder name instead of the ticket worked out from the branch or title.</param>
/// <param name="Folder">Absolute folder to write instead of <c>ReleaseRoot\name</c>.</param>
/// <param name="Clean">First remove files an earlier release run wrote there and nobody has changed since.</param>
/// <param name="ReuseDependencies">Compile against the checkout's DLLs of unchanged projects instead of building them from master.</param>
/// <param name="NoPull">Leave the local base branch and origin's remote-tracking branch where they are instead of bringing them to the fetched tip.</param>
/// <param name="NoLinked">Build only the requested pull request, not the pull requests its description links in other repositories.</param>
internal sealed record ReleaseOptions(string? Name = null, string? Folder = null, bool Clean = false, bool ReuseDependencies = false, bool NoPull = false,
    bool NoLinked = false);

/// <summary>A merged pull request as the GitHub CLI describes it; <see cref="HeadCommit"/> is the head fetched from origin.</summary>
internal sealed record ReleasePullRequest(int Number, string Title, string Author, string Url, string HeadBranch, string BaseBranch,
    string State, string MergeCommit, string MergedAt, string HeadCommit)
{
    public string Label => Title.Length == 0 ? $"PR #{Number}" : $"PR #{Number}: {Title}";
}

/// <summary>Where a pull request's changed file stands on the base branch now.</summary>
internal static class ReleaseStatus
{
    internal const string Same = "Same as the pull request", ChangedAgain = "Also changed by other merged work", NotOnBase = "Not on the base branch",
        Deleted = "Deleted";
}

/// <summary>How a file is released: compiled into its project's DLL, uploaded as is, or left to the developer.</summary>
internal static class ReleaseKind
{
    internal const string Compiled = "Compiled into its project's DLL", Content = "Frontend file", NotIncluded = "Not included",
        ProjectDll = "Project DLL", Package = "Changed package", Sql = "SQL script";
}

/// <summary>What a repository's release is: projects built and content copied, or the SQL scripts of a repository with no project files.</summary>
internal static class ReleaseRepositoryKind
{
    internal const string Code = "Code", Sql = "SQL";
}

internal sealed record ReleaseCommit(string Commit, string Subject);
/// <summary>One file the pull request changed: its change, where it stands on the base branch, and the other merged commits that also changed it.</summary>
internal sealed record ReleaseChange(string Path, string Change, string Status, string Kind, List<ReleaseCommit> Commits);
internal sealed record ReleaseNote(string Path, string Reason);
/// <summary>A project whose DLL is released, and the other merged commits that DLL carries but the pull request branch's own build did not.</summary>
internal sealed record ReleaseProject(string Project, string Assembly, List<ReleaseCommit> OtherMergedChanges);
internal sealed record ReleasePackage(string Name, string Version, string HintPath, string Project);
/// <summary>An unchanged project a released project compiles against: built from the snapshot first, never released.</summary>
internal sealed record SupportProject(string Project, string Assembly, string Reason, bool HasWebfarmTarget, bool Built);
/// <summary>What building did to the developer's local base branch, or in a preview would do to it.</summary>
internal static class ReleasePullOutcome
{
    internal const string Pulled = "Pulled", UpToDate = "Already up to date", Behind = "Behind origin", LocalCommits = "Has local commits",
        NotUpdated = "Not updated", NoBranch = "No local branch", Off = "Pull turned off";
}

/// <summary>
/// The local base branch against the fetched tip: <see cref="Worktree"/> is where it is checked out, if anywhere, <see cref="Before"/> and
/// <see cref="After"/> its commit before and after, and <see cref="Commits"/> the commits it gained, is behind by, or has that origin lacks.
/// </summary>
internal sealed record ReleasePull(string Branch, string? Worktree, string Before, string After, int Commits, string Outcome, string Detail);

/// <summary>A file written to the release folder, as it was verified there.</summary>
internal sealed record ReleaseFile(string Name, string Kind, string Source, long Size, string Sha256, string? AssemblyVersion, bool Replaced);
/// <summary>A file already in the release folder that this run did not write, and whether an earlier release run did.</summary>
internal sealed record ExistingFile(string Name, long Size, string Origin);

/// <summary>A reference a build took from outside its snapshot: a sibling repository, or a DLL in the developer's checkout. In a result,
/// <see cref="Project"/> lists every project that used it.</summary>
internal sealed record OutsideReference(string Name, string Path, string? Version, DateTime LastWriteTime, string Project)
{
    internal static OutsideReference Of(string path, string project) =>
        new(System.IO.Path.GetFileName(path), path, Pipeline.VersionOf(path), File.GetLastWriteTime(path), project);
}

/// <summary>
/// A reference a release project compiled against only because it did not compile against the snapshot's copy: the newer file its HintPath names
/// in the developer's checkout. <see cref="Borrowed"/> is the project whose snapshot copy it replaced, or null for the tracked <c>bin</c> copy.
/// </summary>
internal sealed record SubstitutedReference(string Project, string Name, string Path, string Version, DateTime LastWriteTime, string Replaced, string? Borrowed);

/// <summary>What a release adds to <see cref="Pipeline.Build"/>: the build order including support projects, and the outside references it used.</summary>
internal sealed class SupportBuild(List<ProjectInfo> order)
{
    /// <summary>Every project to build, dependencies first: the plan's projects and the unchanged projects they compile against.</summary>
    public List<ProjectInfo> Order { get; } = order;
    /// <summary>Each reference taken from outside the snapshot, including every <see cref="Substituted"/> one.</summary>
    public List<OutsideReference> Outside { get; } = [];
    /// <summary>
    /// For MyMeyer: each snapshot copy of a reference a project did not compile against, replaced on the one retry by the newer file its HintPath
    /// names in the developer's checkout. Snapshot copies are always tried first.
    /// </summary>
    public List<SubstitutedReference> Substituted { get; } = [];
    /// <summary>
    /// False for a repository other than the MyMeyer checkout: outputs are captured after Build instead of after the webfarm copy target,
    /// references are resolved as MSBuild does in the developer's checkout (HintPath, then the output folder) instead of from MyMeyer's root bin,
    /// packages are never restored, and a reference found nowhere is left to MSBuild and listed in <see cref="Unresolved"/>.
    /// </summary>
    public bool Main { get; init; } = true;
    /// <summary>For another repository: each reference left to MSBuild because Cultivate found it nowhere, as "name (HintPath hint) for project".</summary>
    public List<string> Unresolved { get; } = [];
}

/// <summary>A release preview, or once <see cref="Written"/>, what was built and written. Serialised as the output of <c>cultivate release</c>.</summary>
internal sealed class ReleaseResult
{
    public bool Written { get; set; }
    public string? Note { get; set; }
    public required ReleasePullRequest PullRequest { get; init; }
    /// <summary>The GitHub repository's name ("MyMeyer"), or the checkout's folder name when origin is not on GitHub.</summary>
    public string Repository { get; init; } = "";
    /// <summary>"owner/name", or null when origin is not on GitHub.</summary>
    public string? GitHub { get; init; }
    /// <summary>The developer's checkout of the repository: what is fetched into, pulled, and searched for references the snapshot lacks.</summary>
    public string Checkout { get; init; } = "";
    /// <summary><see cref="ReleaseRepositoryKind.Code"/>, or <see cref="ReleaseRepositoryKind.Sql"/> for a repository with no project files.</summary>
    public string Kind { get; init; } = ReleaseRepositoryKind.Code;
    public string BaseBranch { get; init; } = "";
    /// <summary>The base branch commit everything is built and copied from.</summary>
    public string BaseCommit { get; init; } = "";
    /// <summary>Where the pull request's changes start: the merge base of its head and the base branch before the merge.</summary>
    public string ForkPoint { get; init; } = "";
    /// <summary>The ticket folder's name, decided by the requested pull request.</summary>
    public string Name { get; init; } = "";
    /// <summary>This repository's subfolder of the ticket folder, where its files are written.</summary>
    public string Folder { get; init; } = "";
    public string Subfolder { get; init; } = "";
    /// <summary>Why <see cref="Subfolder"/> is not the usual name: another release in the run already uses that name.</summary>
    public string? Placement { get; init; }
    /// <summary>The pull request URLs the Associated PRs section of its description lists.</summary>
    public List<string> Linked { get; init; } = [];
    public bool Clean { get; init; }
    public bool ReuseDependencies { get; init; }
    public bool NoPull { get; init; }
    /// <summary>The local base branch: what building would do to it, or once <see cref="Written"/>, what building did.</summary>
    public ReleasePull? Pull { get; set; }
    public List<ReleaseChange> Changes { get; init; } = [];
    public List<ReleaseProject> Projects { get; init; } = [];
    public List<ReleasePackage> Packages { get; init; } = [];
    public List<string> Content { get; init; } = [];
    public List<ReleaseNote> NotIncluded { get; init; } = [];
    public List<ReleaseNote> Deleted { get; init; } = [];
    public List<SupportProject> SupportProjects { get; init; } = [];
    public List<OutsideReference> OutsideReferences { get; init; } = [];
    public List<ReleaseFile> Files { get; init; } = [];
    public List<ExistingFile> Existing { get; init; } = [];
    /// <summary>Files <c>--clean</c> removed, or in a preview would remove.</summary>
    public List<string> Removed { get; init; } = [];
    public List<string> Warnings { get; init; } = [];
    public List<string> Errors { get; init; } = [];
    /// <summary>Why this repository's files were not built, in a run that went on to build other repositories; null otherwise.</summary>
    public string? Failure { get; set; }
    /// <summary>The failure itself, for the terminal UI's diagnostics. Not serialised.</summary>
    internal Exception? Exception { get; set; }
    public double Seconds { get; set; }
    public string RunDirectory { get; set; } = "";
    public bool CanBuild => Errors.Count == 0;
}

/// <summary>A linked pull request a run does not build, and why.</summary>
internal sealed record SkippedLink(string Url, string Repository, int Number, string Reason);

/// <summary>
/// Everything one <c>cultivate release</c> builds: the requested pull request's release first, then those of the pull requests its description
/// links, each in its own subfolder of one ticket folder, and the links it skipped. Serialised as the output of <c>cultivate release</c>.
/// </summary>
internal sealed class ReleaseRun
{
    public bool Written { get; set; }
    public string? Note { get; set; }
    /// <summary>The ticket folder's name, from the requested pull request.</summary>
    public string Name { get; init; } = "";
    /// <summary>The ticket folder: one subfolder per repository.</summary>
    public string Folder { get; init; } = "";
    /// <summary>The requested pull request: "owner/name#N", or "#N" when origin is not on GitHub.</summary>
    public string Requested { get; init; } = "";
    public bool NoLinked { get; init; }
    public List<ReleaseResult> Releases { get; init; } = [];
    public List<SkippedLink> Skipped { get; init; } = [];
    public double Seconds { get; set; }
    /// <summary>Whether at least one repository's files can be built; one that cannot is reported and not built.</summary>
    public bool CanBuild => Releases.Any(release => release.CanBuild);
    /// <summary>Whether every repository's files can be built, or once <see cref="Written"/>, were.</summary>
    public bool Complete => Releases.All(release => release.CanBuild && release.Failure is null && (!Written || release.Written));

    /// <summary>A run of one release, as a release built on its own is shown.</summary>
    internal static ReleaseRun Single(ReleaseResult release) => new()
    {
        Written = release.Written, Note = release.Note, Name = release.Name, Folder = Path.GetDirectoryName(release.Folder) ?? release.Folder,
        Requested = $"{release.GitHub}#{release.PullRequest.Number}", Releases = [release], Seconds = release.Seconds
    };
}

/// <summary>Cultivate's record of the files it wrote to one release folder, so later runs know which files are its own.</summary>
internal sealed class ReleaseRecord
{
    public string Folder { get; set; } = "";
    public List<RecordedFile> Files { get; set; } = [];
}
internal sealed record RecordedFile(string Name, string Sha256, int PullRequest, DateTimeOffset WrittenAt);
