using System.Diagnostics;
using System.Net.Http.Json;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Compaction;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace Athlon.Agent.Infrastructure;

public sealed class WorkspaceGuard(IActiveWorkspaceContext workspaceContext, AppSettings settings, IAppPathProvider paths)
{
    public bool HasConfiguredWorkspace => TryGetWorkspaceRoot(out _);

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
        if (!TryGetWorkspaceRoot(out var basePath))
        {
            throw new InvalidOperationException("工作区尚未设定。请先在侧栏「配置」或设置页指定工作区目录。");
        }

        var rooted = Path.IsPathRooted(path) ? path : Path.Combine(cwd ?? basePath, path);
        return Path.GetFullPath(rooted);
    }

    public IReadOnlyList<string> GetIgnorePatterns() =>
        workspaceContext.IgnorePatterns.Count > 0
            ? workspaceContext.IgnorePatterns
            : settings.Workspaces.FirstOrDefault(workspace => !string.IsNullOrWhiteSpace(workspace.RootPath))?.IgnorePatterns
              ?? [".git", "bin", "obj", "node_modules"];

    private bool TryGetWorkspaceRoot(out string rootPath)
    {
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
        if (TryGetWorkspaceRoot(out var workspaceRoot))
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
