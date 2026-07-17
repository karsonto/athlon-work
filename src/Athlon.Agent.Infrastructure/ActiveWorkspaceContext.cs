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

public sealed class ActiveWorkspaceContext : IActiveWorkspaceContext
{
    public string? RootPath { get; private set; }
    public string? DisplayName { get; private set; }
    public IReadOnlyList<string> IgnorePatterns { get; private set; } = WorkspaceIgnoreDefaults.BuiltIn;
    public WorkspaceKind Kind { get; private set; } = WorkspaceKind.Local;
    public string? WorkspaceId { get; private set; }

    public void SetWorkspace(string? rootPath, string? displayName = null, IReadOnlyList<string>? ignorePatterns = null) =>
        SetWorkspace(rootPath, WorkspaceKind.Local, workspaceId: null, displayName, ignorePatterns);

    public void SetWorkspace(
        string? rootPath,
        WorkspaceKind kind,
        string? workspaceId,
        string? displayName = null,
        IReadOnlyList<string>? ignorePatterns = null)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
        {
            RootPath = null;
            DisplayName = null;
            IgnorePatterns = WorkspaceIgnoreDefaults.BuiltIn;
            Kind = WorkspaceKind.Local;
            WorkspaceId = null;
            return;
        }

        Kind = kind;
        WorkspaceId = string.IsNullOrWhiteSpace(workspaceId) ? null : workspaceId.Trim();
        RootPath = kind == WorkspaceKind.Ssh
            ? RemotePathNormalizer.NormalizeRoot(rootPath)
            : Path.GetFullPath(rootPath);
        DisplayName = displayName
            ?? (kind == WorkspaceKind.Ssh
                ? RemotePathNormalizer.GetFileName(RootPath)
                : Path.GetFileName(RootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)));
        IgnorePatterns = ignorePatterns is { Count: > 0 }
            ? ignorePatterns
            : WorkspaceIgnoreDefaults.BuiltIn;
    }
}
