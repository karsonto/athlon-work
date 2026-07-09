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

public sealed class FileListTool(WorkspaceGuard guard, AuditLogService audit) : IAgentTool, IParallelizableAgentTool
{
    public ToolDefinition Definition { get; } = new(
        "file_list",
        "List files in a directory.",
        ToolSchema.Object()
            .String("path", ToolPathDescriptions.OptionalWorkspaceRelativeDirectory)
            .Build());

    public async Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken cancellationToken = default)
    {
        if (!WorkspaceToolHelper.TryResolveOptionalNormalizedPath(invocation, guard, out var fullPath, out var error))
        {
            return error;
        }
        if (!Directory.Exists(fullPath))
        {
            return ToolResult.Failure("Directory not found", fullPath);
        }

        var workspaceRoot = guard.Normalize(".");
        var ignorePatterns = guard.GetIgnorePatterns();
        var files = Directory.EnumerateFileSystemEntries(fullPath, "*", SearchOption.TopDirectoryOnly)
            .Where(path => !WorkspacePathFilter.ShouldIgnorePath(path, ignorePatterns))
            .OrderBy(path => Directory.Exists(path) ? 0 : 1)
            .ThenBy(path => ToolPathNormalizer.ForModel(Path.GetRelativePath(workspaceRoot, path)))
            .Take(200)
            .Select(path => FormatEntry(workspaceRoot, path))
            .ToArray();

        await WorkspaceToolHelper.AuditAsync(audit, "file_list", new { path = fullPath, count = files.Length }, cancellationToken);
        var content = files.Length == 0
            ? "(empty directory)"
            : string.Join(Environment.NewLine, files) + Environment.NewLine + FileListPathCopyNotice;
        var listedDir = ToolPathNormalizer.ForModel(Path.GetRelativePath(workspaceRoot, fullPath));
        if (string.IsNullOrEmpty(listedDir))
        {
            listedDir = ".";
        }

        return ToolResult.Success($"Listed {files.Length} entries from {listedDir}", content);
    }

    private const string FileListPathCopyNotice =
        "(Paths above are exact on-disk names. Copy character-for-character; do not add spaces between English and Chinese in filenames.)";

    private static string FormatEntry(string workspaceRoot, string fullPath)
    {
        var relative = ToolPathNormalizer.ForModel(Path.GetRelativePath(workspaceRoot, fullPath));
        return Directory.Exists(fullPath)
            ? $"[DIR]  {relative}/"
            : $"[FILE] {relative} ({new FileInfo(fullPath).Length} bytes)";
    }
}
