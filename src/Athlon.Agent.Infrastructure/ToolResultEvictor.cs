using System.Text;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Compaction;

namespace Athlon.Agent.Infrastructure;

public sealed class ToolResultEvictor(
    AppSettings settings,
    IFileStorageService storage) : IToolResultEvictor
{
    public async Task<string> EvictIfNeededAsync(
        string sessionId,
        AgentToolCall toolCall,
        ToolResult result,
        string formattedToolContent,
        CancellationToken cancellationToken = default)
    {
        var cfg = settings.ContextCompaction.ToolResultEviction;
        if (!cfg.Enabled)
        {
            return formattedToolContent;
        }

        if (cfg.ExcludedToolNames.Any(name =>
                string.Equals(name, toolCall.Name, StringComparison.OrdinalIgnoreCase)))
        {
            return formattedToolContent;
        }

        var rawContent = result.Content ?? string.Empty;
        if (rawContent.Length <= cfg.MaxResultChars)
        {
            return formattedToolContent;
        }

        var path = await storage.SaveEvictedToolResultAsync(sessionId, toolCall.Id, rawContent, cancellationToken);
        var preview = BuildPreview(rawContent, cfg.PreviewChars);
        var placeholder = new StringBuilder()
            .AppendLine($"[Tool result evicted - {rawContent.Length} chars]")
            .AppendLine($"Archived at: {path}")
            .AppendLine("Preview:")
            .Append(preview)
            .ToString();

        return AgentRuntime.FormatToolResult(
            toolCall,
            ToolResult.Success(result.Summary, placeholder));
    }

    private static string BuildPreview(string content, int previewChars)
    {
        if (content.Length <= previewChars * 2)
        {
            return content;
        }

        var head = content[..previewChars];
        var tail = content[^previewChars..];
        return head + "\n...\n" + tail;
    }
}
