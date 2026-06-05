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

public sealed class FileWriteTool(WorkspaceGuard guard, AuditLogService audit) : IAgentTool
{
    public ToolDefinition Definition { get; } = new("file_write", "Create or overwrite a file with backup.", new Dictionary<string, string> { ["path"] = ToolPathDescriptions.WorkspaceRelativePath, ["content"] = "New content" }, RequiresApproval: true);

    public async Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken cancellationToken = default)
    {
        if (!ToolArguments.TryGetNormalizedPath(invocation, out var path, out var error)) return error;
        if (!ToolArguments.TryGetRequired(invocation, "content", out var content, out error)) return error;

        var fullPath = guard.Normalize(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        AtomicFile.BackupIfExists(fullPath);
        await File.WriteAllTextAsync(fullPath, content, cancellationToken);
        await audit.WriteAsync("file_write", new { path = fullPath, chars = content.Length }, cancellationToken);
        return ToolResult.Success($"Wrote {content.Length} chars to {Path.GetFileName(fullPath)}");
    }
}
