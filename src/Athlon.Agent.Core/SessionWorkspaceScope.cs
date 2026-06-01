namespace Athlon.Agent.Core;

/// <summary>
/// Per-async-flow workspace root for concurrent session turns. Takes precedence over <see cref="IActiveWorkspaceContext"/>.
/// </summary>
public sealed class SessionWorkspaceScope : IDisposable
{
    private static readonly AsyncLocal<SessionWorkspaceState?> Current = new();

    private readonly SessionWorkspaceState? _previous;

    private SessionWorkspaceScope(string? rootPath, IReadOnlyList<string> ignorePatterns)
    {
        _previous = Current.Value;
        Current.Value = new SessionWorkspaceState(rootPath, ignorePatterns);
    }

    public static SessionWorkspaceState? CurrentState => Current.Value;

    public static IDisposable Enter(string? rootPath, IReadOnlyList<string>? ignorePatterns = null)
    {
        var patterns = ignorePatterns is { Count: > 0 }
            ? ignorePatterns
            : WorkspaceIgnoreDefaults.BuiltIn;
        return new SessionWorkspaceScope(rootPath, patterns);
    }

    public void Dispose() => Current.Value = _previous;

    public sealed class SessionWorkspaceState
    {
        public SessionWorkspaceState(string? rootPath, IReadOnlyList<string> ignorePatterns)
        {
            RootPath = string.IsNullOrWhiteSpace(rootPath) ? null : Path.GetFullPath(rootPath);
            IgnorePatterns = ignorePatterns;
        }

        public string? RootPath { get; }
        public IReadOnlyList<string> IgnorePatterns { get; }
    }
}
