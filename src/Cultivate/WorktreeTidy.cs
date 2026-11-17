namespace Cultivate;

/// <summary>Lists every repository's worktrees with their size and age, and deletes the ones chosen.</summary>
internal sealed class WorktreeTidy(Settings settings, GitRepository git)
{
    /// <summary>The home screen's Tidy worktrees action.</summary>
    internal Task Run(DeploymentState? state)
    {
        _ = (settings, git, state);
        throw new InvalidOperationException("Tidying worktrees is not available in this build.");
    }

    /// <summary><c>cultivate worktrees</c></summary>
    internal static Task<int> Command(Settings settings, GitRepository git, string[] args) =>
        throw new InvalidOperationException("Tidying worktrees is not available in this build.");
}
