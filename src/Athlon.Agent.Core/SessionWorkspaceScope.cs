namespace Athlon.Agent.Core;

/// <summary>
/// Per-async-flow workspace root for concurrent session turns. Takes precedence over <see cref="IActiveWorkspaceContext"/>.
/// </summary>
public sealed class SessionWorkspaceScope : IDisposable
{
    private static readonly AsyncLocal<SessionWorkspaceState?> Current = new();

    private readonly SessionWorkspaceState? _previous;

    private SessionWorkspaceScope(string? rootPath, IReadOnlyList<string> ignorePatterns, WorkspaceKind kind)
    {
        _previous = Current.Value;
        Current.Value = new SessionWorkspaceState(rootPath, ignorePatterns, kind);
    }

    public static SessionWorkspaceState? CurrentState => Current.Value;

    public static IDisposable Enter(
        string? rootPath,
        IReadOnlyList<string>? ignorePatterns = null,
        WorkspaceKind kind = WorkspaceKind.Local)
    {
        var patterns = ignorePatterns is { Count: > 0 }
            ? ignorePatterns
            : WorkspaceIgnoreDefaults.BuiltIn;
        return new SessionWorkspaceScope(rootPath, patterns, kind);
    }

    public void Dispose() => Current.Value = _previous;

    public sealed class SessionWorkspaceState
    {
        public SessionWorkspaceState(string? rootPath, IReadOnlyList<string> ignorePatterns, WorkspaceKind kind)
        {
            Kind = kind;
            RootPath = string.IsNullOrWhiteSpace(rootPath)
                ? null
                : kind == WorkspaceKind.Ssh
                    ? RemotePathNormalizer.NormalizeRoot(rootPath)
                    : Path.GetFullPath(rootPath);
            IgnorePatterns = ignorePatterns;
        }

        public string? RootPath { get; }
        public IReadOnlyList<string> IgnorePatterns { get; }
        public WorkspaceKind Kind { get; }
    }
}
