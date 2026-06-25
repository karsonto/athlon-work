using Athlon.Agent.Core;

namespace Athlon.Agent.Infrastructure;

public sealed class FileReadTool(WorkspaceGuard guard, AuditLogService audit, AppSettings settings) : IAgentTool, IParallelizableAgentTool
{
    public ToolDefinition Definition { get; } = new(
        "file_read",
        "Read file content with line numbers (N|line) for display. Large files require offset/limit; "
            + "use grep_files to locate content first. Do not use N| prefixes in file_edit old_text.",
        new Dictionary<string, string>
        {
            ["path"] = ToolPathDescriptions.WorkspaceRelativePath,
            ["offset"] = "Optional 0-indexed start line. Default: 0",
            ["limit"] = $"Optional max lines (default {FileReadSettingsDefaults.DefaultLineLimit}, max {FileReadSettingsDefaults.MaxLinesPerCall})",
            ["start_line"] = "Optional 1-indexed start line",
            ["end_line"] = "Optional 1-indexed end line (inclusive)"
        });

    public async Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken cancellationToken = default)
    {
        if (!WorkspaceToolHelper.TryResolveNormalizedPath(invocation, guard, out var fullPath, out var error))
        {
            return error;
        }
        if (!File.Exists(fullPath))
        {
            return ToolResult.Failure("File not found", fullPath);
        }

        var fileRead = settings.FileRead;
        var fileInfo = new FileInfo(fullPath);
        if (fileInfo.Length > fileRead.MaxFileBytes)
        {
            return ToolResult.Failure(
                $"File exceeds {fileRead.MaxFileBytes} bytes — use grep_files to search, or read with offset/limit in smaller chunks",
                fullPath);
        }

        var selection = FileReadLineReader.ResolveSelection(invocation, fileRead);
        var read = await FileReadLineReader.ReadAsync(fullPath, selection, fileRead, cancellationToken);
        await WorkspaceToolHelper.AuditAsync(
            audit,
            "file_read",
            new
            {
                path = fullPath,
                totalLines = read.TotalLines,
                linesReturned = read.LinesReturned,
                offset = read.Offset,
                truncated = read.Truncated
            },
            cancellationToken);

        var summary = FileReadLineReader.BuildSummary(Path.GetFileName(fullPath), read);
        var content = read.LinesReturned == 0 && string.IsNullOrEmpty(read.Body)
            ? "(no lines in range)"
            : read.Body;

        return ToolResult.Success(summary, content);
    }
}

internal static class FileReadSettingsDefaults
{
    public const int DefaultLineLimit = 500;
    public const int MaxLinesPerCall = 2_000;
}
