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
    public ToolDefinition Definition { get; } = new(
        "file_edit",
        "Replace exact text in a file (with backup). old_text must match disk content exactly — not file_read's N|line prefixes.",
        new Dictionary<string, string>
        {
            ["path"] = ToolPathDescriptions.WorkspaceRelativePath,
            ["old_text"] = "Exact substring from the file (no line-number prefixes)",
            ["new_text"] = "Replacement",
            ["replace_all"] = "Optional true to replace all occurrences"
        },
        RequiresApproval: true);

    public async Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken cancellationToken = default)
    {
        if (!WorkspaceToolHelper.TryResolveNormalizedPath(invocation, guard, out var fullPath, out var error)) return error;
        if (!ToolArguments.TryGetRequired(invocation, "old_text", out var oldText, out error)) return error;
        if (!ToolArguments.TryGetRequired(invocation, "new_text", out var newText, out error)) return error;
        var content = await File.ReadAllTextAsync(fullPath, cancellationToken);
        var replaceAll = invocation.Arguments.TryGetValue("replace_all", out var value) && bool.TryParse(value, out var parsed) && parsed;

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
        AtomicFile.BackupIfExists(fullPath);
        var updated = FileEditMatcher.ApplyReplace(content, match.MatchedOldText, effectiveNewText, replaceAll);
        await File.WriteAllTextAsync(fullPath, updated, cancellationToken);
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
        return ToolResult.Success($"Edited {Path.GetFileName(fullPath)} ({(replaceAll ? match.Occurrences : 1)} replacement(s))");
    }
}
