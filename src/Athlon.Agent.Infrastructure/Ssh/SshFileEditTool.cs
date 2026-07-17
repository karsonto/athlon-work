using Athlon.Agent.Core;
using Athlon.Agent.Infrastructure;

namespace Athlon.Agent.Infrastructure.Ssh;

public sealed class SshFileEditTool(
    WorkspaceGuard guard,
    ISshWorkspaceClient client,
    AuditLogService audit) : IAgentTool, IRemoteWorkspaceTool
{
    public const long MaxFileEditBytes = FileEditTool.MaxFileEditBytes;

    public ToolDefinition Definition { get; } = new(
        "file_edit",
        "Replace exact text in a file. old_text must match disk content exactly — not file_read's N|line prefixes. "
            + "If matching fails, use apply_patch with a unified diff instead. "
            + $"For files larger than {MaxFileEditBytes / 1024} KiB, use apply_patch.",
        ToolSchema.Object()
            .String("path", ToolPathDescriptions.WorkspaceRelativePath, required: true, minLength: 1)
            .String("old_text", "Exact substring from the file (no line-number prefixes)", required: true, minLength: 1)
            .String("new_text", "Replacement (empty string deletes matched text)", required: true)
            .Boolean("replace_all", "Replace all occurrences", defaultValue: false)
            .Build(),
        RequiresApproval: true);

    public async Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken cancellationToken = default)
    {
        if (!SshWorkspaceToolHelper.TryResolveNormalizedPath(invocation, guard, client, out var fullPath, out var error))
        {
            return error;
        }

        if (!ToolArguments.TryGetRequired(invocation, "old_text", out var oldText, out error))
        {
            return error;
        }

        var newText = invocation.Arguments.GetString("new_text") ?? string.Empty;
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

            if (info.Length > MaxFileEditBytes)
            {
                return ToolResult.Failure(
                    "File too large for file_edit",
                    $"File is {info.Length:N0} bytes, exceeding the {MaxFileEditBytes / 1024} KiB file_edit threshold. Use apply_patch instead.");
            }

            var content = await client.ReadTextAsync(fullPath, cancellationToken).ConfigureAwait(false);
            var replaceAll = invocation.Arguments.GetBoolean("replace_all");
            var match = FileEditMatcher.TryMatch(content, oldText, replaceAll);
            switch (match.Status)
            {
                case FileEditMatchStatus.NotFound:
                    return ToolResult.Failure("Text not found", FileEditMatcher.BuildNotFoundMessage(oldText));
                case FileEditMatchStatus.NotUnique:
                    return ToolResult.Failure(
                        "Text is not unique",
                        "old_text must match exactly once unless replace_all is true.");
            }

            var effectiveNewText = FileEditMatcher.ResolveNewText(oldText, newText, match);
            var updated = FileEditMatcher.ApplyReplace(content, match.MatchedOldText, effectiveNewText, replaceAll);
            await client.WriteTextAsync(fullPath, updated, cancellationToken).ConfigureAwait(false);
            await WorkspaceToolHelper.AuditAsync(
                audit,
                "file_edit",
                new
                {
                    path = fullPath,
                    oldChars = match.MatchedOldText.Length,
                    newChars = effectiveNewText.Length,
                    occurrences = replaceAll ? match.Occurrences : 1,
                    remote = true
                },
                cancellationToken).ConfigureAwait(false);
            return ToolResult.Success($"Edited {RemotePathNormalizer.GetFileName(fullPath)}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ToolResult.Failure("Edit failed", ex.Message);
        }
    }
}
