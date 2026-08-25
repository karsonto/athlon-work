namespace Athlon.Agent.Core;

public sealed record ScheduleTurnOptions(
    string? ModelNameOverride = null,
    bool AllowToolCalls = true,
    int? MaxModelToolRounds = null,
    /// <summary>null = no schedule restriction; non-null = allow only these skill names.</summary>
    IReadOnlyList<string>? SkillNames = null,
    /// <summary>null = no schedule restriction; non-null = allow only these MCP server names.</summary>
    IReadOnlyList<string>? McpServerNames = null);

public sealed class ScheduleTurnScope : IDisposable
{
    private static readonly AsyncLocal<ScheduleTurnScope?> Ambient = new();

    private readonly ScheduleTurnScope? _previous;

    private ScheduleTurnScope(ScheduleTurnOptions options)
    {
        _previous = Ambient.Value;
        ModelNameOverride = options.ModelNameOverride;
        AllowToolCalls = options.AllowToolCalls;
        MaxModelToolRounds = options.MaxModelToolRounds;
        SkillNames = options.SkillNames;
        McpServerNames = options.McpServerNames;
        Ambient.Value = this;
    }

    public static ScheduleTurnScope? Current => Ambient.Value;

    public string? ModelNameOverride { get; }
    public bool AllowToolCalls { get; }
    public int? MaxModelToolRounds { get; }
    public IReadOnlyList<string>? SkillNames { get; }
    public IReadOnlyList<string>? McpServerNames { get; }

    public static bool IsMcpServerAllowed(string? serverName)
    {
        var allowed = Current?.McpServerNames;
        if (allowed is null)
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(serverName))
        {
            return false;
        }

        return allowed.Any(name =>
            string.Equals(name, serverName, StringComparison.OrdinalIgnoreCase));
    }

    public static IDisposable Enter(ScheduleTurnOptions options) => new ScheduleTurnScope(options);

    public void Dispose() => Ambient.Value = _previous;
}
