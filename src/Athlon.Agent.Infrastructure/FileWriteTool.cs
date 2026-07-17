using System.Text.Json;
using Athlon.Agent.Core;

namespace Athlon.Agent.Infrastructure;

public sealed class FileWriteTool(WorkspaceGuard guard, AuditLogService audit) : IAgentTool, ILocalWorkspaceTool
{
    public ToolDefinition Definition { get; } = new(
        "file_write",
        "Create or overwrite a file. `content` must be a non-empty string with the full file body.",
        ToolSchema.Object()
            .String("path", ToolPathDescriptions.WorkspaceRelativePath, required: true, minLength: 1)
            .String(
                "content",
                "Full new file content as a JSON string (non-empty). Do not omit, null, or send a non-string type.",
                required: true,
                minLength: 1)
            .Build(),
        RequiresApproval: true);

    public async Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken cancellationToken = default)
    {
        if (!WorkspaceToolHelper.TryResolveNormalizedPath(invocation, guard, out var fullPath, out var error))
        {
            return EnrichFailure(invocation, error);
        }

        if (!TryGetContent(invocation, out var content, out error))
        {
            return error;
        }

        var modelPath = invocation.Arguments.GetString(ToolPathNormalizer.PathArgumentName) ?? fullPath;

        try
        {
            if (Directory.Exists(fullPath))
            {
                return ToolResult.Failure(
                    "Path is a directory",
                    $"Cannot write file: '{ToolPathNormalizer.ForModel(modelPath)}' is a directory.");
            }

            var parent = Path.GetDirectoryName(fullPath);
            if (string.IsNullOrEmpty(parent))
            {
                return ToolResult.Failure(
                    "Invalid path",
                    $"Cannot determine parent directory for '{ToolPathNormalizer.ForModel(modelPath)}'.");
            }

            Directory.CreateDirectory(parent);
            await AtomicFile.WriteAllTextAsync(fullPath, content, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return ToolResult.Failure("Write failed", DescribeWriteException(ex, modelPath));
        }

        await WorkspaceToolHelper.AuditAsync(
            audit,
            "file_write",
            new { path = fullPath, chars = content.Length },
            cancellationToken);

        var fileName = Path.GetFileName(fullPath);
        return ToolResult.Success($"Wrote {content.Length} chars to {fileName}");
    }

    /// <summary>
    /// Resolves <c>content</c> for writing. Returns structured parameter errors so the model can retry
    /// when the argument was omitted, empty, or not a JSON string (common streaming truncation).
    /// </summary>
    private static bool TryGetContent(ToolInvocation invocation, out string content, out ToolResult error)
    {
        content = string.Empty;

        if (!invocation.Arguments.TryGetValue("content", out var element))
        {
            error = ToolInvocationErrors.Failure(
                "Invalid tool arguments",
                new ToolInvocationError(
                    "file_write.content.missing",
                    "$.content",
                    "non-empty JSON string with the full file body",
                    "missing",
                    "Include `content` as a JSON string in the tool arguments object, e.g. "
                    + "{\"path\":\"src/a.cs\",\"content\":\"using System;\\n...\"}. "
                    + "Do not omit content; an empty file is not allowed."));
            return false;
        }

        if (element.ValueKind == JsonValueKind.Null
            || element.ValueKind == JsonValueKind.Undefined)
        {
            error = ToolInvocationErrors.Failure(
                "Invalid tool arguments",
                new ToolInvocationError(
                    "file_write.content.null",
                    "$.content",
                    "non-empty JSON string",
                    "null",
                    "Pass the file body as a JSON string. Null is not valid for file_write.content."));
            return false;
        }

        if (element.ValueKind != JsonValueKind.String)
        {
            error = ToolInvocationErrors.Failure(
                "Invalid tool arguments",
                new ToolInvocationError(
                    "file_write.content.type_mismatch",
                    "$.content",
                    "JSON string",
                    DescribeJsonKind(element),
                    "Serialize the entire file body as one JSON string value for `content`. "
                    + "Do not pass a number, boolean, object, or array. "
                    + "If arguments JSON was truncated during generation, regenerate the tool call with complete `content`."));
            return false;
        }

        content = element.GetString() ?? string.Empty;
        if (content.Length == 0)
        {
            error = ToolInvocationErrors.Failure(
                "Invalid tool arguments",
                new ToolInvocationError(
                    "file_write.content.empty",
                    "$.content",
                    "non-empty string (minLength 1)",
                    "\"\" (empty string)",
                    "Provide the full file contents in `content`. Creating zero-byte files via file_write is not supported."));
            return false;
        }

        error = ToolResult.Success("OK");
        return true;
    }

    private static string DescribeJsonKind(JsonElement element) =>
        element.ValueKind switch
        {
            JsonValueKind.Number => $"number ({element.GetRawText()})",
            JsonValueKind.True or JsonValueKind.False => $"boolean ({element.GetRawText()})",
            JsonValueKind.Object => "object",
            JsonValueKind.Array => "array",
            JsonValueKind.Null => "null",
            _ => element.ValueKind.ToString()
        };

    private static ToolResult EnrichFailure(ToolInvocation invocation, ToolResult error)
    {
        if (error.Succeeded)
        {
            return error;
        }

        var modelPath = invocation.Arguments.GetString(ToolPathNormalizer.PathArgumentName);
        return error.Summary switch
        {
            "Outside workspace" =>
                ToolResult.Failure(
                    error.Summary,
                    $"Path '{ToolPathNormalizer.ForModel(modelPath ?? string.Empty)}' is outside the active workspace. "
                        + "Use a workspace-relative path (e.g. src/foo.cs)."),
            "Invalid path" =>
                ToolResult.Failure(
                    error.Summary,
                    error.Error ?? $"Invalid path '{modelPath}'. Use a workspace-relative path with forward slashes."),
            "Missing argument" when error.Error?.Contains(ToolPathNormalizer.PathArgumentName, StringComparison.Ordinal) == true =>
                ToolResult.Failure(
                    error.Summary,
                    "file_write requires `path` (workspace-relative, forward slashes)."),
            _ => error
        };
    }

    private static string DescribeWriteException(Exception ex, string modelPath)
    {
        var displayPath = ToolPathNormalizer.ForModel(modelPath);
        return ex switch
        {
            UnauthorizedAccessException =>
                $"Access denied writing '{displayPath}'. Check file permissions or whether it is read-only.",
            DirectoryNotFoundException =>
                $"Parent directory for '{displayPath}' does not exist and could not be created.",
            IOException io when io.Message.Contains("used by another process", StringComparison.OrdinalIgnoreCase) =>
                $"File '{displayPath}' is locked by another process. Close the handle and retry.",
            PathTooLongException =>
                $"Path too long: '{displayPath}'.",
            IOException io =>
                $"I/O error writing '{displayPath}': {io.Message}",
            _ =>
                $"Failed to write '{displayPath}': {ex.Message}"
        };
    }
}
