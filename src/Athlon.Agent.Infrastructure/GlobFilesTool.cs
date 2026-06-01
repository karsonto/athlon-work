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

public sealed class GlobFilesTool(WorkspaceGuard guard, AuditLogService audit) : IAgentTool
{
    public ToolDefinition Definition { get; } = new(
        "glob_files",
        "Find workspace files matching a glob pattern.",
        new Dictionary<string, string>
        {
            ["pattern"] = "Glob pattern, e.g. **/*.cs",
            ["path"] = ToolPathDescriptions.OptionalWorkspaceRelativeDirectory
        });

    public async Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken cancellationToken = default)
    {
        if (!ToolArguments.TryGetRequired(invocation, "pattern", out var pattern, out var error)) return error;
        if (!ToolArguments.TryGetOptionalNormalizedPath(invocation, out var requestedPath, out error)) return error;
        var fullPath = guard.Normalize(requestedPath);
        if (!guard.IsInsideWorkspace(fullPath)) return ToolResult.Failure("Outside workspace", fullPath);
        if (!Directory.Exists(fullPath)) return ToolResult.Failure("Directory not found", fullPath);

        var searchPattern = pattern.Contains('/') || pattern.Contains('\\') ? Path.GetFileName(pattern) : pattern;
        var recursive = pattern.Contains("**", StringComparison.Ordinal);
        var ignorePatterns = guard.GetIgnorePatterns();
        var matches = Directory.EnumerateFileSystemEntries(fullPath, searchPattern.Replace("**", "*"), recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly)
            .Where(path => !WorkspacePathFilter.ShouldIgnorePath(path, ignorePatterns))
            .Take(200)
            .Select(path => Directory.Exists(path)
                ? $"{Path.GetRelativePath(fullPath, path)}/"
                : $"{Path.GetRelativePath(fullPath, path)} ({new FileInfo(path).Length} bytes)")
            .ToArray();

        await audit.WriteAsync("glob_files", new { path = fullPath, pattern, count = matches.Length }, cancellationToken);
        return matches.Length == 0
            ? ToolResult.Success("No matching files found", "No matching files found")
            : ToolResult.Success($"Found {matches.Length} matching entries", string.Join(Environment.NewLine, matches));
    }
}
