using System.Text;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Memory;
using Athlon.Agent.Core.Prompt;

namespace Athlon.Agent.Infrastructure.Memory;

/// <summary>
/// Injects the curated MEMORY.md content into the system prompt before each reasoning iteration.
/// Only when memory is enabled and MEMORY.md is non-empty.
/// </summary>
public sealed class MemoryPromptContributor(ILongTermMemory longTermMemory, AppSettings settings) : IPreReasoningPromptContributor
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(5);
    private readonly MemorySettings _cfg = settings.Memory;
    private readonly object _cacheLock = new();
    private string _cachedMemoryContent = string.Empty;
    private DateTimeOffset _nextRefreshAt = DateTimeOffset.MinValue;
    private Task? _refreshTask;

    public int Priority => 40; // after workspace files, before skills

    public void Append(StringBuilder builder, EnvironmentPromptContext context)
    {
        if (!_cfg.Enabled || PromptModeHelper.IsChatOnly(context))
            return;

        StartRefreshIfNeeded();
        var memoryContent = _cachedMemoryContent;
        if (string.IsNullOrWhiteSpace(memoryContent))
            return;

        builder.AppendLine();
        builder.AppendLine("## Long-Term Memory");
        builder.AppendLine();
        builder.AppendLine("Below is the consolidated long-term memory from previous sessions. Use it to recall user preferences, past decisions, and persistent context.");
        builder.AppendLine();
        builder.AppendLine("<long_term_memory>");
        builder.AppendLine(memoryContent.Trim());
        builder.AppendLine("</long_term_memory>");
        builder.AppendLine();
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
