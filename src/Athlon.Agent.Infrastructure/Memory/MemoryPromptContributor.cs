using System.Text;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Harness;
using Athlon.Agent.Core.Memory;
using Athlon.Agent.Core.Prompt;

namespace Athlon.Agent.Infrastructure.Memory;

/// <summary>
/// Optionally injects curated MEMORY.md into the system prompt before each reasoning iteration.
/// Only when harness is enabled for the session. Default mode is None (use memory_search / memory_get).
/// </summary>
public sealed class MemoryPromptContributor(
    ILongTermMemory longTermMemory,
    ISessionHarnessState harnessState,
    IAgentRunContextAccessor runContextAccessor,
    AppSettings settings) : IPreReasoningPromptContributor
{
    private const string TruncationNotice = "\n\n...(truncated — use memory_get)...";
    private const string PreviewHint = "Use memory_search / memory_get for full memory.";

    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(5);
    private readonly object _cacheLock = new();
    private string _cachedMemoryContent = string.Empty;
    private DateTimeOffset _nextRefreshAt = DateTimeOffset.MinValue;
    private Task? _refreshTask;

    public int Priority => 40; // after workspace files, before skills

    public void Append(StringBuilder builder, EnvironmentPromptContext context)
    {
        if (!harnessState.IsCodingModeForActiveRun(runContextAccessor) || PromptModeHelper.IsChatOnly(context))
        {
            return;
        }

        var mode = settings.Memory.InlinePromptMode;
        if (mode == MemoryInlinePromptMode.None)
        {
            return;
        }

        EnsureMemoryLoaded();
        StartRefreshIfNeeded();
        var memoryContent = _cachedMemoryContent;
        if (string.IsNullOrWhiteSpace(memoryContent))
        {
            return;
        }

        var inlineContent = ResolveInlineContent(memoryContent.Trim(), mode);
        if (string.IsNullOrWhiteSpace(inlineContent))
        {
            return;
        }

        builder.AppendLine();
        builder.AppendLine("## Long-Term Memory");
        builder.AppendLine();
        if (mode == MemoryInlinePromptMode.Preview)
        {
            builder.AppendLine(PreviewHint);
            builder.AppendLine();
        }
        else
        {
            builder.AppendLine("Below is the consolidated long-term memory from previous sessions. Use it to recall user preferences, past decisions, and persistent context.");
            builder.AppendLine();
        }

        builder.AppendLine("<long_term_memory>");
        builder.AppendLine(inlineContent);
        builder.AppendLine("</long_term_memory>");
        builder.AppendLine();
    }

    public static string ResolveInlineContent(string memoryContent, MemoryInlinePromptMode mode, int maxPreviewChars = 800, int maxFullChars = 16_000)
    {
        if (string.IsNullOrWhiteSpace(memoryContent))
        {
            return string.Empty;
        }

        return mode switch
        {
            MemoryInlinePromptMode.None => string.Empty,
            MemoryInlinePromptMode.Preview => Truncate(memoryContent, maxPreviewChars, appendNotice: false),
            MemoryInlinePromptMode.Full => Truncate(memoryContent, maxFullChars, appendNotice: true),
            _ => string.Empty
        };
    }

    private string ResolveInlineContent(string memoryContent, MemoryInlinePromptMode mode)
    {
        var maxFullChars = Math.Max(256, settings.Memory.MaxMemoryTokens * 4);
        return ResolveInlineContent(memoryContent, mode, settings.Memory.MaxInlineMemoryChars, maxFullChars);
    }

    public static string Truncate(string content, int maxChars, bool appendNotice)
    {
        if (content.Length <= maxChars)
        {
            return content;
        }

        var truncated = content[..maxChars];
        return appendNotice ? truncated + TruncationNotice : truncated;
    }

    private void EnsureMemoryLoaded()
    {
        if (!string.IsNullOrWhiteSpace(_cachedMemoryContent))
        {
            return;
        }

        try
        {
            _cachedMemoryContent = longTermMemory.ReadCuratedAsync().GetAwaiter().GetResult();
        }
        catch
        {
            // Prompt construction must not fail on memory I/O.
        }
    }

    private void StartRefreshIfNeeded()
    {
        lock (_cacheLock)
        {
            var now = DateTimeOffset.UtcNow;
            if (_refreshTask is { IsCompleted: false } || now < _nextRefreshAt)
            {
                return;
            }

            _nextRefreshAt = now.Add(RefreshInterval);
            _refreshTask = RefreshCacheAsync();
        }
    }

    private async Task RefreshCacheAsync()
    {
        try
        {
            var memoryContent = await longTermMemory.ReadCuratedAsync().ConfigureAwait(false);
            _cachedMemoryContent = memoryContent;
        }
        catch
        {
            // Keep the last good snapshot; prompt construction must not fail on memory I/O.
        }
    }
}
