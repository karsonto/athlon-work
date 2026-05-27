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
    private static readonly string[] DefaultIgnorePatterns = [".git", "bin", "obj", "node_modules", ".vs", "artifacts", "publish"];

    public string? RootPath { get; private set; }
    public string? DisplayName { get; private set; }
    public IReadOnlyList<string> IgnorePatterns { get; private set; } = DefaultIgnorePatterns;

    public void SetWorkspace(string? rootPath, string? displayName = null, IReadOnlyList<string>? ignorePatterns = null)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
        {
            RootPath = null;
            DisplayName = null;
            IgnorePatterns = DefaultIgnorePatterns;
            return;
        }

        RootPath = Path.GetFullPath(rootPath);
        DisplayName = displayName ?? Path.GetFileName(RootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        IgnorePatterns = ignorePatterns ?? DefaultIgnorePatterns;
    }
}
