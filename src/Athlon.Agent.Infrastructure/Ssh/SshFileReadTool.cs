using Athlon.Agent.Core;
using Athlon.Agent.Infrastructure;

namespace Athlon.Agent.Infrastructure.Ssh;

public sealed class SshFileReadTool(
    WorkspaceGuard guard,
    ISshWorkspaceClient client,
    AuditLogService audit,
    AppSettings settings) : IAgentTool, IRemoteWorkspaceTool, IParallelizableAgentTool
{
    public ToolDefinition Definition { get; } = new(
        "file_read",
        "Read file content with line numbers (N|line) for display. Use 1-based start_line/end_line ranges for large files; "
            + "use grep_files to locate content first. Do not use N| prefixes in file_edit old_text.",
        ToolSchema.Object()
            .String("path", ToolPathDescriptions.WorkspaceRelativePath, required: true, minLength: 1)
            .Integer("start_line", "1-based start line (default 1)", defaultValue: 1, minimum: 1)
            .Integer("end_line", $"1-based end line, inclusive (default start_line + {FileReadSettingsDefaults.DefaultLineLimit - 1}; max {FileReadSettingsDefaults.MaxLinesPerCall} lines per call)", minimum: 1)
            .Build());

    public async Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken cancellationToken = default)
    {
        if (!SshWorkspaceToolHelper.TryResolveNormalizedPath(invocation, guard, client, out var fullPath, out var error))
        {
            return error;
        }

        try
        {
            var info = await client.TryGetFileInfoAsync(fullPath, cancellationToken).ConfigureAwait(false);
            if (info is null)
            {
                return ToolResult.Failure("File not found", fullPath);
            }

            if (info.IsDirectory)
            {
                return ToolResult.Failure("Path is a directory", fullPath);
            }

            var fileRead = settings.FileRead;
            if (info.Length > fileRead.MaxFileBytes)
            {
                return ToolResult.Failure(
                    "File exceeds the configured size limit — use grep_files to locate the needed range, then read with start_line/end_line",
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

            var selection = FileReadLineReader.ResolveSelection(invocation, fileRead);
            // Stream line-by-line over SFTP so we do not materialize the whole remote file in memory
            // when only a window is needed (and can stop early when CountTotalLines is false).
            var read = await client.ReadViaStreamAsync(
                    fullPath,
                    (stream, ct) => FileReadLineReader.ReadFromStreamAsync(stream, selection, fileRead, ct),
                    cancellationToken)
                .ConfigureAwait(false);

            await WorkspaceToolHelper.AuditAsync(
                audit,
                "file_read",
                new
                {
                    path = fullPath,
                    totalLines = read.TotalLines,
                    linesReturned = read.LinesReturned,
                    startLine = read.StartLine,
                    nextStartLine = read.NextStartLine,
                    truncated = read.Truncated,
                    remote = true
                },
                cancellationToken).ConfigureAwait(false);

            var summary = FileReadLineReader.BuildSummary(RemotePathNormalizer.GetFileName(fullPath), read);
            var content = read.LinesReturned == 0 && string.IsNullOrEmpty(read.Body)
                ? "(no lines in range)"
                : read.Body;
            return ToolResult.Success(summary, content);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ToolResult.Failure("Read failed", ex.Message);
        }
    }
}
