namespace Cultivate;

/// <summary>
/// What deleting a worktree destroys. Tidying always keeps the branch, so commits on a branch survive whether or not they are pushed;
/// only uncommitted changes, and the unpushed commits of a detached worktree that no branch holds, exist nowhere else.
/// </summary>
internal sealed record TidyLoss(int Uncommitted, int Commits)
{
    public static TidyLoss Of(Checkout checkout, CheckoutStatus status) => new(status.Uncommitted, checkout.Detached ? status.Unpushed : 0);

    public bool None => Uncommitted == 0 && Commits == 0;

    /// <summary>Whether deleting now would lose more than <paramref name="confirmed"/>, which the developer accepted.</summary>
    public bool Exceeds(TidyLoss confirmed) => Uncommitted > confirmed.Uncommitted || Commits > confirmed.Commits;

    /// <summary>"3 uncommitted changes and 2 commits that are on no branch or remote", or "nothing".</summary>
    public override string ToString() => None ? "nothing" : string.Join(" and ", new[]
    {
        Uncommitted == 0 ? "" : Uncommitted == 1 ? "1 uncommitted change" : $"{Uncommitted} uncommitted changes",
        Commits == 0 ? "" : Commits == 1 ? "1 commit that is on no branch or remote" : $"{Commits} commits that are on no branch or remote"
    }.Where(part => part.Length != 0));

    /// <summary>"Its 2 unpushed commits stay on branch 12345.", for a branch worktree whose commits are not pushed; otherwise null.</summary>
    public static string? KeptOnBranch(Checkout checkout, CheckoutStatus status) =>
        checkout.Detached || status.Unpushed == 0 ? null :
        $"Its {(status.Unpushed == 1 ? "1 unpushed commit stays" : $"{status.Unpushed} unpushed commits stay")} on branch {checkout.Branch}.";
}
