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

public sealed class FileListTool(WorkspaceGuard guard, AuditLogService audit) : IAgentTool
{
    public ToolDefinition Definition { get; } = new(
        "file_list",
        "List files in the active workspace or a workspace subdirectory.",
        new Dictionary<string, string> { ["path"] = ToolPathDescriptions.OptionalWorkspaceRelativeDirectory });

    public async Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken cancellationToken = default)
    {
        if (!ToolArguments.TryGetOptionalNormalizedPath(invocation, out var requestedPath, out var error))
        {
            return error;
        }

        var fullPath = guard.Normalize(requestedPath);
        if (!guard.IsInsideWorkspace(fullPath))
        {
            return ToolResult.Failure("Outside workspace", fullPath);
        }

        if (!Directory.Exists(fullPath))
        {
            return ToolResult.Failure("Directory not found", fullPath);
        }

        var ignorePatterns = guard.GetIgnorePatterns();
        var files = Directory.EnumerateFileSystemEntries(fullPath, "*", SearchOption.TopDirectoryOnly)
            .Where(path => !WorkspacePathFilter.ShouldIgnoreEntryName(Path.GetFileName(path), ignorePatterns))
            .OrderBy(path => Directory.Exists(path) ? 0 : 1)
            .ThenBy(Path.GetFileName)
            .Take(200)
            .Select(path => Directory.Exists(path) ? $"[DIR]  {Path.GetFileName(path)}" : $"[FILE] {Path.GetFileName(path)} ({new FileInfo(path).Length} bytes)")
            .ToArray();

        await audit.WriteAsync("file_list", new { path = fullPath, count = files.Length }, cancellationToken);
        var content = files.Length == 0 ? "(empty directory)" : string.Join(Environment.NewLine, files);
        return ToolResult.Success($"Listed {files.Length} entries from {Path.GetFileName(fullPath)}", content);
    }
}
