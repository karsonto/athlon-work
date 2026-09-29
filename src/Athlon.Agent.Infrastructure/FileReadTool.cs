using Athlon.Agent.Core;

namespace Athlon.Agent.Infrastructure;

public sealed class FileReadTool(WorkspaceGuard guard, AuditLogService audit, AppSettings settings) : IAgentTool, IParallelizableAgentTool, ILocalWorkspaceTool
{
    public ToolDefinition Definition { get; } = new(
        "file_read",
        "Read file content exactly as stored on disk. The body has no line-number prefixes; "
            + "line numbers are only in the footer (start_line, end_line, next_start_line). "
            + "Paths may be inside or outside the workspace. "
            + "Use 1-based start_line/end_line to read in chunks; "
            + "do not assume a single read covers the whole file. When the result has truncated:true or next_start_line, "
            + "continue from that 1-based line. Prefer grep_files/glob_files to locate content in large files first. "
            + "file_edit old_text must copy this body, not a line-number prefix.",
        ToolSchema.Object()
            .String("path", "Absolute or workspace-relative file path to read.", required: true, minLength: 1)
            .Integer("start_line", "1-based start line (default 1)", defaultValue: 1, minimum: 1)
            .Integer("end_line", $"1-based end line, inclusive (default start_line + {FileReadSettingsDefaults.DefaultLineLimit - 1}; max {FileReadSettingsDefaults.MaxLinesPerCall} lines per call)", minimum: 1)
            .Build());

    public async Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken cancellationToken = default)
    {
        if (!WorkspaceToolHelper.TryResolveNormalizedPath(
                invocation,
                guard,
                out var fullPath,
                out var error,
                requireInsideWorkspace: false))
        {
            return error;
        }
        if (!File.Exists(fullPath))
        {
            return ToolResult.Failure(
                SimilarFileNameSuggester.FormatNotFound(SuggestSimilarNames(fullPath)),
                fullPath);
        }
        if (invocation.Arguments.TryGetInt32("start_line", out var startLine)
            && invocation.Arguments.TryGetInt32("end_line", out var endLine)
            && endLine < startLine)
        {
            return ToolInvocationErrors.Failure(
                "Invalid line range",
                new ToolInvocationError(
                    "file_read.invalid_range",
                    "$.end_line",
                    $">= start_line ({startLine})",
                    endLine.ToString(),
                    "Set end_line to the same value as start_line or a later 1-based line number."));
        }

        var fileRead = settings.FileRead;
        var fileInfo = new FileInfo(fullPath);
        if (fileInfo.Length > fileRead.MaxFileBytes)
        {
            return ToolResult.Failure(
                "File exceeds the configured size limit — use grep_files to locate the needed range, then read with start_line/end_line",
                fullPath);
        }

        var selection = FileReadLineReader.ResolveSelection(invocation, fileRead);
        var read = await FileReadLineReader.ReadAsync(fullPath, selection, fileRead, cancellationToken);
        await WorkspaceToolHelper.AuditAsync(
            audit,
            "file_read",
            new
            {
                path = WorkspaceToolHelper.ToAuditPath(guard, fullPath),
                totalLines = read.TotalLines,
                linesReturned = read.LinesReturned,
                startLine = read.StartLine,
                nextStartLine = read.NextStartLine,
                truncated = read.Truncated
            },
            cancellationToken);

        var summary = FileReadLineReader.BuildSummary(Path.GetFileName(fullPath), read);
        var content = read.LinesReturned == 0 && string.IsNullOrEmpty(read.Body)
            ? "(no lines in range)"
            : read.Body;

        return ToolResult.Success(summary, content);
    }

    private static IReadOnlyList<string> SuggestSimilarNames(string fullPath)
    {
        var directory = Path.GetDirectoryName(fullPath);
        var name = Path.GetFileName(fullPath);
        if (string.IsNullOrEmpty(directory) || string.IsNullOrEmpty(name) || !Directory.Exists(directory))
        {
            return [];
        }

        try
        {
            var names = Directory.EnumerateFileSystemEntries(directory)
                .Select(Path.GetFileName)
                .OfType<string>()
                .Take(400);
            return SimilarFileNameSuggester.Suggest(name, names);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}

internal static class FileReadSettingsDefaults
{
    public const int DefaultLineLimit = 500;
    public const int MaxLinesPerCall = 2_000;
}
