namespace Cultivate;

/// <summary>One external build step: its name, the tool and arguments, where it runs, and the log file its whole output goes to.</summary>
internal sealed record OnlineCommand(string Step, string Tool, IReadOnlyList<string> Arguments, string Directory, string Log);

/// <summary>Where the local master stands against origin's, after a normal fetch: its commit (empty when there is none), origin's, and the
/// commits it lacks and has that origin does not.</summary>
internal sealed record OnlineMaster(string Local, string Origin, int Behind, int Ahead, string Detail);

/// <summary>The publish profile as read: the configuration and folder the publish uses, and every property it sets.</summary>
internal sealed record OnlineProfile(string Path, string Configuration, string PublishUrl, string PublishFolder, Dictionary<string, string> Properties);

internal sealed record OnlineStepTime(string Step, double Seconds);

/// <summary>A MeyerOnline release preview, or once <see cref="Built"/>, what was built. Serialised as the output of <c>cultivate online-release</c>.</summary>
internal sealed class OnlineReleaseResult
{
    public bool Built { get; set; }
    public string? Note { get; set; }
    public string Repository { get; init; } = "";
    /// <summary>The branch every release is built from: always master, never origin's default branch.</summary>
    public string Branch { get; init; } = OnlineRelease.Branch;
    /// <summary>The branch checked out when this was read, or <c>(detached abc1234)</c>.</summary>
    public string CurrentBranch { get; init; } = "";
    public OnlineMaster? Master { get; init; }
    /// <summary>A merge, rebase, cherry-pick, revert or bisect in progress, if any.</summary>
    public string? Operation { get; init; }
    /// <summary>Modified or staged tracked files. They stop the build unless <see cref="AllowLocalChanges"/>.</summary>
    public List<string> ModifiedFiles { get; init; } = [];
    public List<string> UntrackedFiles { get; init; } = [];
    public bool AllowLocalChanges { get; init; }
    public OnlineProfile? Profile { get; set; }
    public bool NodeModules { get; init; }
    /// <summary>The dotnet and npm found on PATH, or null.</summary>
    public string? Dotnet { get; init; }
    public string? Npm { get; init; }
    public string Zip { get; init; } = "";
    public bool ZipExists { get; init; }
    public List<string> Warnings { get; init; } = [];
    public List<string> Errors { get; init; } = [];
    public bool Switched { get; set; }
    public string? SwitchedFrom { get; set; }
    /// <summary>Commits the pull brought into master.</summary>
    public int Pulled { get; set; }
    public string Commit { get; set; } = "";
    public string Subject { get; set; } = "";
    public bool Installed { get; set; }
    public string? InstallReason { get; set; }
    /// <summary>The <c>lastupdated</c> value of the front end's version.json.</summary>
    public string? Version { get; set; }
    public long Size { get; set; }
    public int Entries { get; set; }
    public string Sha256 { get; set; } = "";
    public List<OnlineStepTime> Steps { get; init; } = [];
    public List<string> Logs { get; init; } = [];
    public double Seconds { get; set; }
    public string RunDirectory { get; set; } = "";
    public bool CanBuild => Errors.Count == 0;
}
