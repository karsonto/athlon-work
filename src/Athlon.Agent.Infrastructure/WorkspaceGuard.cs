using Athlon.Agent.Core;

namespace Athlon.Agent.Infrastructure;

public sealed class WorkspaceGuard(IActiveWorkspaceContext workspaceContext, AppSettings settings, IAppPathProvider paths)
{
    public bool HasConfiguredWorkspace => TryGetWorkspaceRoot(out _);

    public bool TryGetWorkspaceRoot(out string rootPath) => TryGetWorkspaceRootInternal(out rootPath);

    public bool IsInsideWorkspace(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var fullPath = Path.GetFullPath(path);
        return GetAllowedRoots().Any(root => IsPathUnderRoot(fullPath, root));
    }

    public string Normalize(string path, string? cwd = null)
    {
        if (!TryGetWorkspaceRootInternal(out var basePath))
        {
            throw new InvalidOperationException("工作区尚未设定。请先在侧栏「配置」或设置页指定工作区目录。");
        }

        path = ToolPathNormalizer.ForModel(path);
        var rooted = Path.IsPathRooted(path) ? path : Path.Combine(cwd ?? basePath, path);
        return Path.GetFullPath(rooted);
    }

    public IReadOnlyList<string> GetIgnorePatterns()
    {
        var scoped = SessionWorkspaceScope.CurrentState;
        if (scoped is not null && scoped.IgnorePatterns.Count > 0)
        {
            return scoped.IgnorePatterns;
        }

        return workspaceContext.IgnorePatterns.Count > 0
            ? workspaceContext.IgnorePatterns
            : settings.Workspaces.FirstOrDefault(workspace => !string.IsNullOrWhiteSpace(workspace.RootPath))?.IgnorePatterns
              ?? [".git", "bin", "obj", "node_modules"];
    }

    private bool TryGetWorkspaceRootInternal(out string rootPath)
    {
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

        rootPath = configured.RootPath;
        return true;
    }

    private IEnumerable<string> GetAllowedRoots()
    {
        if (TryGetWorkspaceRootInternal(out var workspaceRoot))
        {
            yield return workspaceRoot;
        }

        if (!string.IsNullOrWhiteSpace(paths.RootPath))
        {
            yield return paths.RootPath;
        }
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
