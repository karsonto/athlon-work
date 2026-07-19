using System.Text;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Compaction;
using Athlon.Agent.Core.Memory;
using Athlon.Agent.Core.Prompt;

namespace Athlon.Agent.Infrastructure.Memory;

/// <summary>
/// Optionally injects curated MEMORY.md into ephemeral runtime context before each reasoning iteration.
/// Requires an active workspace. Default mode is None (use memory_search / memory_get).
/// </summary>
public sealed class MemoryPromptContributor(
    ILongTermMemory longTermMemory,
    IActiveWorkspaceContext workspaceContext,
    IActiveAgentSessionContext sessionContext,
    AppSettings settings) : IRuntimeContextContributor
{
    private const string TruncationNotice = "\n\n...(truncated — use memory_get)...";
    private const string PreviewHint = "Use memory_search / memory_get for full memory. Memory is scoped to the current project session.";

    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(5);
    private readonly object _cacheLock = new();
    private string _cachedMemoryContent = string.Empty;
    private string? _cachedScopeKey;
    private DateTimeOffset _nextRefreshAt = DateTimeOffset.MinValue;
    private Task? _refreshTask;

    public int Priority => 40; // after workspace files, before skills

    public void Append(StringBuilder builder, EnvironmentPromptContext context)
    {
        if (PromptModeHelper.IsChatOnly(context)
            || (!HasWorkspace() || !longTermMemory.HasActiveScope))
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
        builder.AppendLine("## Long-Term Memory (current project session)");
        builder.AppendLine();
        if (mode == MemoryInlinePromptMode.Preview)
        {
            builder.AppendLine(PreviewHint);
            builder.AppendLine();
        }
        else
        {
            builder.AppendLine("Below is the consolidated long-term memory for this conversation session. Use it to recall user preferences, past decisions, and persistent context.");
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
        var maxFullChars = Math.Max(
            256,
            ContextTokenEstimator.EstimateCharacterBudget(settings.Memory.MaxMemoryTokens));
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

    private string CurrentScopeKey()
    {
        MemoryScopeResolver.TryResolve(workspaceContext, sessionContext, out var workspaceKey, out var sessionId);
        return $"{workspaceKey}|{sessionId}";
    }

    private void EnsureMemoryLoaded()
    {
        var scopeKey = CurrentScopeKey();
        lock (_cacheLock)
        {
            if (!string.Equals(_cachedScopeKey, scopeKey, StringComparison.Ordinal))
            {
                _cachedScopeKey = scopeKey;
                _cachedMemoryContent = string.Empty;
                _nextRefreshAt = DateTimeOffset.MinValue;
            }
        }

        if (!string.IsNullOrWhiteSpace(_cachedMemoryContent))
        {
            return;
        }

        try
        {
            _cachedMemoryContent = longTermMemory.ReadCuratedAsync().GetAwaiter().GetResult();
            _cachedScopeKey = scopeKey;
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
            var scopeKey = CurrentScopeKey();
            _refreshTask = RefreshCacheAsync(scopeKey);
        }
    }

    private async Task RefreshCacheAsync(string scopeKey)
    {
        try
        {
            var memoryContent = await longTermMemory.ReadCuratedAsync().ConfigureAwait(false);
            lock (_cacheLock)
            {
                if (string.Equals(_cachedScopeKey, scopeKey, StringComparison.Ordinal)
                    || string.IsNullOrEmpty(_cachedScopeKey))
                {
                    _cachedScopeKey = scopeKey;
                    _cachedMemoryContent = memoryContent;
                }
            }
        }
        catch
        {
            // Keep the last good snapshot; prompt construction must not fail on memory I/O.
        }
    }

    private bool HasWorkspace() =>
        !string.IsNullOrWhiteSpace(workspaceContext.RootPath)
        || !string.IsNullOrWhiteSpace(workspaceContext.WorkspaceId);
}
