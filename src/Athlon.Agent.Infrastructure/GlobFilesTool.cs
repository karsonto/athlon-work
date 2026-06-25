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

public sealed class GlobFilesTool(WorkspaceGuard guard, AuditLogService audit) : IAgentTool, IParallelizableAgentTool
{
    public ToolDefinition Definition { get; } = new(
        "glob_files",
        "Find files matching a glob pattern.",
        new Dictionary<string, string>
        {
            ["pattern"] = "Glob pattern (supports ** and {a,b} extensions), e.g. **/*.cs or **/*.{png,jpg}",
            ["path"] = ToolPathDescriptions.OptionalWorkspaceRelativeDirectory
        });

    public async Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken cancellationToken = default)
    {
        if (!ToolArguments.TryGetRequired(invocation, "pattern", out var pattern, out var error)) return error;
        if (!WorkspaceToolHelper.TryResolveOptionalNormalizedPath(invocation, guard, out var fullPath, out error)) return error;
        if (!Directory.Exists(fullPath)) return ToolResult.Failure("Directory not found", fullPath);

        var ignorePatterns = guard.GetIgnorePatterns();
        var matches = GlobPatternHelper.EnumerateMatches(fullPath, pattern, ignorePatterns)
            .Select(path => Directory.Exists(path)
                ? $"{Path.GetRelativePath(fullPath, path)}/"
                : $"{Path.GetRelativePath(fullPath, path)} ({new FileInfo(path).Length} bytes)")
            .ToArray();

        await WorkspaceToolHelper.AuditAsync(audit, "glob_files", new { path = fullPath, pattern, count = matches.Length }, cancellationToken);
        return matches.Length == 0
            ? ToolResult.Success("No matching files found", "No matching files found")
            : ToolResult.Success($"Found {matches.Length} matching entries", string.Join(Environment.NewLine, matches));
    }
}
