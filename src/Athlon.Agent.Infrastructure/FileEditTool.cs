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

public sealed class FileEditTool(WorkspaceGuard guard, AuditLogService audit) : IAgentTool
{
    public ToolDefinition Definition { get; } = new("file_edit", "Replace text in a workspace file with backup. old_text must be unique unless replace_all is true.", new Dictionary<string, string> { ["path"] = "File path", ["old_text"] = "Unique text", ["new_text"] = "Replacement", ["replace_all"] = "Optional true to replace all occurrences" }, RequiresApproval: true);

    public async Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken cancellationToken = default)
    {
        if (!ToolArguments.TryGetRequired(invocation, "path", out var path, out var error)) return error;
        if (!ToolArguments.TryGetRequired(invocation, "old_text", out var oldText, out error)) return error;
        if (!ToolArguments.TryGetRequired(invocation, "new_text", out var newText, out error)) return error;

        var fullPath = guard.Normalize(path);
        if (!guard.IsInsideWorkspace(fullPath)) return ToolResult.Failure("Outside workspace", fullPath);
        var content = await File.ReadAllTextAsync(fullPath, cancellationToken);
        var replaceAll = invocation.Arguments.TryGetValue("replace_all", out var value) && bool.TryParse(value, out var parsed) && parsed;
        var occurrences = content.Split(oldText).Length - 1;
        if (occurrences == 0) return ToolResult.Failure("Text not found", "old_text did not match.");
        if (!replaceAll && occurrences != 1) return ToolResult.Failure("Text is not unique", "old_text must match exactly once unless replace_all is true.");
        AtomicFile.BackupIfExists(fullPath);
        var updated = replaceAll ? content.Replace(oldText, newText) : ReplaceFirst(content, oldText, newText);
        await File.WriteAllTextAsync(fullPath, updated, cancellationToken);
        await audit.WriteAsync("file_edit", new { path = fullPath, oldChars = oldText.Length, newChars = newText.Length, occurrences = replaceAll ? occurrences : 1 }, cancellationToken);
        return ToolResult.Success($"Edited {Path.GetFileName(fullPath)} ({(replaceAll ? occurrences : 1)} replacement(s))");
    }

    private static string ReplaceFirst(string content, string oldText, string newText)
    {
        var index = content.IndexOf(oldText, StringComparison.Ordinal);
        return index < 0 ? content : content[..index] + newText + content[(index + oldText.Length)..];
    }
}
