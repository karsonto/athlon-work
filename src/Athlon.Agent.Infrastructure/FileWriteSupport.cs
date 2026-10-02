using System.Text.Json;
using Athlon.Agent.Core;

namespace Athlon.Agent.Infrastructure;

internal static class FileWriteSupport
{
    /// <summary>
    /// Resolves <c>content</c> for writing. Returns structured parameter errors so the model can retry
    /// when the argument was omitted, empty, or not a JSON string (common streaming truncation).
    /// </summary>
    public static bool TryGetContent(ToolInvocation invocation, out string content, out ToolResult error)
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

    /// <summary>
    /// Audit runs after the file has been read back. A log failure must not turn that write into a tool failure.
    /// </summary>
    public static async Task TryAuditAsync(
        AuditLogService audit,
        string toolName,
        object payload,
        CancellationToken cancellationToken)
    {
        try
        {
            await WorkspaceToolHelper.AuditAsync(audit, toolName, payload, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
        }
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
}
