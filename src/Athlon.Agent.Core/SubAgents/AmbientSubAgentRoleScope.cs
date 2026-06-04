namespace Athlon.Agent.Core.SubAgents;

public sealed class AmbientSubAgentRoleScope : IDisposable
{
    private static readonly AsyncLocal<string?> Current = new();

    private readonly string? _previous;

    private AmbientSubAgentRoleScope(string role)
    {
        _previous = Current.Value;
        Current.Value = role;
    }

    public static string? CurrentRole => Current.Value;

    public static IDisposable Enter(string role) => new AmbientSubAgentRoleScope(role);

    public void Dispose() => Current.Value = _previous;
}
