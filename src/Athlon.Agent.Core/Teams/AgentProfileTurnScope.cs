namespace Athlon.Agent.Core.Teams;

public sealed record AgentProfileTurnOptions(
    string ProfileId,
    string? ModelNameOverride = null,
    int? MaxModelToolRounds = null,
    IReadOnlyList<string>? SkillNames = null,
    IReadOnlyList<string>? McpServerNames = null,
    IReadOnlyList<string>? ToolAllowList = null,
    IReadOnlyList<string>? KnowledgeModuleIds = null,
    bool IsLeader = false,
    string? TeamTaskId = null,
    string? RootSessionId = null);

/// <summary>
/// Ambient profile capability filter for Team member turns.
/// Independent from <see cref="ScheduleTurnScope"/> so schedules are unaffected.
/// </summary>
public sealed class AgentProfileTurnScope : IDisposable
{
    private static readonly AsyncLocal<AgentProfileTurnScope?> Ambient = new();

    private readonly AgentProfileTurnScope? _previous;

    private AgentProfileTurnScope(AgentProfileTurnOptions options)
    {
        _previous = Ambient.Value;
        Options = options;
        Ambient.Value = this;
    }

    public static AgentProfileTurnScope? Current => Ambient.Value;

    public AgentProfileTurnOptions Options { get; }

    public static IDisposable Enter(AgentProfileTurnOptions options) => new AgentProfileTurnScope(options);

    public static bool IsToolAllowed(string? toolName)
    {
        var allowed = Current?.Options.ToolAllowList;
        if (allowed is null)
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(toolName))
        {
            return false;
        }

        // Empty allow-list means "use caller-provided default set" — treat as unrestricted here;
        // ProfileToolRouter applies the concrete default set when constructing the router.
        if (allowed.Count == 0)
        {
            return true;
        }

        return allowed.Any(name =>
            string.Equals(name, toolName, StringComparison.OrdinalIgnoreCase)
            || (name.EndsWith("*", StringComparison.Ordinal) &&
                toolName.StartsWith(name[..^1], StringComparison.OrdinalIgnoreCase)));
    }

    public static bool IsMcpServerAllowed(string? serverName)
    {
        if (Current is null)
        {
            return ScheduleTurnScope.IsMcpServerAllowed(serverName);
        }

        var allowed = Current.Options.McpServerNames;
        if (allowed is null)
        {
            return ScheduleTurnScope.IsMcpServerAllowed(serverName);
        }

        if (string.IsNullOrWhiteSpace(serverName))
        {
            return false;
        }

        // Empty allow-list matches UI "0 MCP" — deny all servers for this profile turn.
        if (allowed.Count == 0)
        {
            return false;
        }

        return allowed.Any(name =>
            string.Equals(name, serverName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// When a profile scope is active: null means unrestricted; empty means deny all skills;
    /// non-empty is an allow-list.
    /// </summary>
    public static IReadOnlyList<string>? SkillNames => Current?.Options.SkillNames;

    public void Dispose() => Ambient.Value = _previous;
}
