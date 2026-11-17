namespace Cultivate;

/// <summary>Groups every repository's checkouts by ticket, so one ticket's work is seen and opened together.</summary>
internal sealed class TicketWorkspaces(Settings settings, GitRepository git, Bridge bridge, Terminal terminal)
{
    /// <summary>The home screen's Ticket workspaces action.</summary>
    internal Task Run(DeploymentState? state)
    {
        _ = (settings, git, bridge, terminal, state);
        throw new InvalidOperationException("Ticket workspaces are not available in this build.");
    }

    /// <summary><c>cultivate tickets</c></summary>
    internal static Task<int> Command(Settings settings, GitRepository git, string[] args) =>
        throw new InvalidOperationException("Ticket workspaces are not available in this build.");
}
