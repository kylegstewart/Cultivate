namespace Cultivate;

/// <summary>What the dev site answered when it was requested after a deployment.</summary>
internal sealed record WarmupResult(string Url, bool Ready, int? Status, double Seconds, int Attempts, string Summary, string Detail = "");

/// <summary>Requests the dev site after a deployment, so Cultivate waits for the recompilation instead of the developer's first page load.</summary>
internal static class Warmup
{
    /// <summary>A first guess at the dev site's address from its webfarm share, or "" when none can be made.</summary>
    internal static string SuggestSiteUrl(string webfarmRoot) =>
        Uri.TryCreate(webfarmRoot, UriKind.Absolute, out var root) && root.IsUnc ? $"http://{root.Host}/" : "";

    /// <summary>Shown in the terminal UI after an activation's summary.</summary>
    internal static Task AfterActivation(Settings settings, DeploymentState activated) => Task.CompletedTask;

    /// <summary>Runs after <c>cultivate activate</c>, before its JSON result is printed.</summary>
    internal static Task AfterActivationCommand(Settings settings, DeploymentState activated, string[] args) => Task.CompletedTask;

    /// <summary><c>cultivate warmup</c></summary>
    internal static Task<int> Command(Settings settings, string[] args) =>
        throw new InvalidOperationException("The warm-up check is not available in this build.");
}
