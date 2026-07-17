using Athlon.Agent.Core;

namespace Athlon.Agent.Infrastructure;

public sealed class WorkspaceGuard(
    IActiveWorkspaceContext workspaceContext,
    IAgentRunContextAccessor runContextAccessor,
    AppSettings settings,
    IAppPathProvider paths)
{
    public bool HasConfiguredWorkspace => TryGetWorkspaceRoot(out _);

    public WorkspaceKind CurrentKind
    {
        get
        {
            var runContext = runContextAccessor.Current;
            if (runContext is not null)
            {
                return runContext.WorkspaceKind;
            }

            var scoped = SessionWorkspaceScope.CurrentState;
            if (scoped is not null)
            {
                return scoped.Kind;
            }

            return workspaceContext.Kind;
        }
    }

    public bool TryGetWorkspaceRoot(out string rootPath) => TryGetWorkspaceRootInternal(out rootPath);

    public bool IsInsideWorkspace(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        if (CurrentKind == WorkspaceKind.Ssh)
        {
            if (!TryGetWorkspaceRootInternal(out var remoteRoot))
            {
                return false;
            }

            return RemotePathNormalizer.IsUnderRoot(path, remoteRoot);
        }

        var fullPath = Path.GetFullPath(path);
        return GetAllowedRoots().Any(root => IsPathUnderRoot(fullPath, root));
    }

    public string Normalize(string path, string? cwd = null)
    {
        if (CurrentKind == WorkspaceKind.Ssh)
        {
            return NormalizeRemote(path, cwd);
        }

        path = ToolPathNormalizer.ForModel(path);

        if (TryGetWorkspaceRootInternal(out var basePath))
        {
            path = ToolPathNormalizer.ResolveRelativeToWorkspaceRoot(path, basePath);
            var rootedFromWorkspace = Path.IsPathRooted(path) ? path : Path.Combine(cwd ?? basePath, path);
            return Path.GetFullPath(rootedFromWorkspace);
        }

        if (Path.IsPathRooted(path))
        {
            return Path.GetFullPath(path);
        }

        var fallbackBase = string.IsNullOrWhiteSpace(cwd) ? Environment.CurrentDirectory : cwd;
        return Path.GetFullPath(Path.Combine(fallbackBase, path));
    }

    private string NormalizeRemote(string path, string? cwd)
    {
        path = RemotePathNormalizer.ForModel(path);
        if (!TryGetWorkspaceRootInternal(out var remoteRoot))
        {
            return RemotePathNormalizer.Collapse(path.StartsWith('/') ? path : "/" + path);
        }

        if (path.StartsWith('/'))
        {
            return RemotePathNormalizer.Collapse(path);
        }

        var basePath = string.IsNullOrWhiteSpace(cwd) ? remoteRoot : RemotePathNormalizer.Combine(remoteRoot, cwd);
        return RemotePathNormalizer.Combine(basePath, path);
    }

    public IReadOnlyList<string> GetIgnorePatterns()
    {
        var runContext = runContextAccessor.Current;
        if (runContext?.WorkspaceIgnorePatterns is { Count: > 0 } runPatterns)
        {
            return runPatterns;
        }

        var scoped = SessionWorkspaceScope.CurrentState;
        if (scoped is not null && scoped.IgnorePatterns.Count > 0)
        {
            return scoped.IgnorePatterns;
        }

        if (workspaceContext.RootPath is not null && workspaceContext.IgnorePatterns is { Count: > 0 })
        {
            return workspaceContext.IgnorePatterns;
        }

        WorkspaceSettings? configuredWorkspace = null;
        if (TryGetWorkspaceRootInternal(out var workspaceRoot))
        {
            configuredWorkspace = settings.Workspaces.FirstOrDefault(workspace =>
                !string.IsNullOrWhiteSpace(workspace.RootPath)
                && RootsEqual(workspace.RootPath, workspaceRoot, workspace.WorkspaceKind));
        }

        return WorkspaceIgnoreResolver.Resolve(
            workspacePatterns: configuredWorkspace?.IgnorePatterns,
            globalPatterns: settings.WorkspaceIgnore.DirectoryNames);
    }

    private bool TryGetWorkspaceRootInternal(out string rootPath)
    {
        var runRoot = runContextAccessor.Current?.WorkspaceRoot;
        if (runRoot is { Length: > 0 })
        {
            rootPath = runRoot;
            return true;
        }

        var scoped = SessionWorkspaceScope.CurrentState;
        if (scoped?.RootPath is { Length: > 0 } scopedRoot)
        {
            rootPath = scopedRoot;
            return true;
        }

        if (!string.IsNullOrWhiteSpace(workspaceContext.RootPath))
        {
            rootPath = workspaceContext.RootPath;
            return true;
        }

        var configured = settings.Workspaces.FirstOrDefault(workspace => !string.IsNullOrWhiteSpace(workspace.RootPath));
        if (configured is null)
        {
            rootPath = string.Empty;
            return false;
        }

        rootPath = configured.WorkspaceKind == WorkspaceKind.Ssh
            ? RemotePathNormalizer.NormalizeRoot(configured.RootPath)
            : configured.RootPath;
        return true;
    }

    private IEnumerable<string> GetAllowedRoots()
    {
        if (TryGetWorkspaceRootInternal(out var workspaceRoot))
        {
            yield return workspaceRoot;
        }

        if (!string.IsNullOrWhiteSpace(paths.RootPath) && CurrentKind != WorkspaceKind.Ssh)
        {
            yield return paths.RootPath;
        }
    }

    private static bool RootsEqual(string configuredRoot, string activeRoot, WorkspaceKind kind)
    {
        if (kind == WorkspaceKind.Ssh)
        {
            return string.Equals(
                RemotePathNormalizer.NormalizeRoot(configuredRoot),
                RemotePathNormalizer.NormalizeRoot(activeRoot),
                StringComparison.Ordinal);
        }

        return string.Equals(Path.GetFullPath(configuredRoot), activeRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPathUnderRoot(string fullPath, string rootPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
        {
            return false;
        }

        var normalizedRoot = Path.GetFullPath(rootPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var normalizedPath = Path.GetFullPath(fullPath);

        return normalizedPath.Equals(normalizedRoot.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)
               || normalizedPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
    }
}
