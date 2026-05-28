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

public sealed class FileReadTool(WorkspaceGuard guard, AuditLogService audit) : IAgentTool
{
    public ToolDefinition Definition { get; } = new(
        "file_read",
        "Read workspace file content with line numbers (N|line) for display. Do not use those prefixes in file_edit old_text.",
        new Dictionary<string, string>
        {
            ["path"] = "File path",
            ["offset"] = "Optional 0-indexed start line. Default: 0",
            ["limit"] = "Optional max lines to return. Default: all lines",
            ["start_line"] = "Optional 1-indexed start line",
            ["end_line"] = "Optional 1-indexed end line"
        });

    public async Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken cancellationToken = default)
    {
        if (!ToolArguments.TryGetRequired(invocation, "path", out var path, out var error)) return error;
        var fullPath = guard.Normalize(path);
        if (!guard.IsInsideWorkspace(fullPath)) return ToolResult.Failure("Outside workspace", fullPath);
        if (!File.Exists(fullPath)) return ToolResult.Failure("File not found", fullPath);
        var lines = await File.ReadAllLinesAsync(fullPath, cancellationToken);
        var selected = SelectLines(lines, invocation).ToArray();
        await audit.WriteAsync("file_read", new { path = fullPath, lines = lines.Length }, cancellationToken);
        return ToolResult.Success($"Read {selected.Length} of {lines.Length} lines from {Path.GetFileName(fullPath)}", string.Join(Environment.NewLine, selected));
    }

    private static IEnumerable<string> SelectLines(string[] lines, ToolInvocation invocation)
    {
        var offset = ToolArguments.GetInt32(invocation, "offset", 0);
        var limit = ToolArguments.GetInt32(invocation, "limit", 0);
        var startLine = ToolArguments.GetInt32(invocation, "start_line", 0);
        var endLine = ToolArguments.GetInt32(invocation, "end_line", 0);

        if (startLine > 0)
        {
            offset = Math.Max(0, startLine - 1);
            limit = endLine >= startLine ? endLine - startLine + 1 : 0;
        }

        var selected = lines
            .Skip(Math.Max(0, offset))
            .Take(limit <= 0 ? 500 : Math.Min(limit, 500))
            .Select((line, index) => $"{offset + index + 1}|{line}");

        return selected;
    }
}
