using Athlon.Agent.Core;

namespace Athlon.Agent.Infrastructure;

public sealed class FileEditTool(WorkspaceGuard guard, AuditLogService audit) : IAgentTool, ILocalWorkspaceTool
{
    /// <summary>Maximum file size in bytes for file_edit. Larger files should use apply_patch.</summary>
    public const long MaxFileEditBytes = 512 * 1024;

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
        if (!WorkspaceToolHelper.TryResolveNormalizedPath(invocation, guard, out var fullPath, out var error))
        {
            return error;
        }

        if (!ToolArguments.TryGetRequired(invocation, "old_text", out var oldText, out error))
        {
            return error;
        }

        if (!TryGetNewText(invocation, out var newText, out error))
        {
            return error;
        }

        if (!File.Exists(fullPath))
        {
            return ToolResult.Failure("File not found", fullPath);
        }

        // Threshold check: large files should use apply_patch instead
        var fileInfo = new FileInfo(fullPath);
        if (fileInfo.Length > MaxFileEditBytes)
        {
            var displayPath = ToolPathNormalizer.ForModel(invocation.Arguments.GetString(ToolPathNormalizer.PathArgumentName) ?? fullPath);
            return ToolResult.Failure(
                "File too large for file_edit",
                $"File '{displayPath}' is {fileInfo.Length:N0} bytes, exceeding the {MaxFileEditBytes / 1024} KiB file_edit threshold. "
                    + "Use apply_patch with a unified diff instead.");
        }

        string content;
        try
        {
            content = await File.ReadAllTextAsync(fullPath, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ToolResult.Failure("Read failed", $"Could not read '{ToolPathNormalizer.ForModel(invocation.Arguments.GetString(ToolPathNormalizer.PathArgumentName) ?? fullPath)}': {ex.Message}");
        }

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

        try
        {
            await AtomicFile.WriteAllTextAsync(fullPath, updated, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var modelPath = ToolPathNormalizer.ForModel(invocation.Arguments.GetString(ToolPathNormalizer.PathArgumentName) ?? fullPath);
            return ToolResult.Failure("Write failed", $"Failed to write '{modelPath}': {ex.Message}");
        }

        await WorkspaceToolHelper.AuditAsync(
            audit,
            "file_edit",
            new
            {
                path = fullPath,
                oldChars = match.MatchedOldText.Length,
                newChars = effectiveNewText.Length,
                occurrences = replaceAll ? match.Occurrences : 1,
                normalized = match.Kind != OldTextCandidateKind.Exact
            },
            cancellationToken);

        // Generate diff for display
        var relativePath = Path.GetRelativePath(guard.Normalize("."), fullPath)
            .Replace('\\', '/');
        var diff = UnifiedDiffGenerator.Generate(content, updated, relativePath);

        var replacementCount = replaceAll ? match.Occurrences : 1;
        var summary = effectiveNewText.Length == 0
            ? $"Deleted text in {Path.GetFileName(fullPath)} ({replacementCount} replacement(s))"
            : $"Edited {Path.GetFileName(fullPath)} ({replacementCount} replacement(s))";
        return ToolResult.Success(summary, diff);
    }

    private static bool TryGetNewText(ToolInvocation invocation, out string newText, out ToolResult error)
    {
        if (!invocation.Arguments.TryGetString("new_text", out newText!))
        {
            newText = string.Empty;
            error = ToolResult.Failure(
                "Missing argument",
                "file_edit requires `new_text`. Pass \"\" (empty string) to delete the matched text.");
            return false;
        }

        newText ??= string.Empty;
        error = ToolResult.Success("OK");
        return true;
    }
}
