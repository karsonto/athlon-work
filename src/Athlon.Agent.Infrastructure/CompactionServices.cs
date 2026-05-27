using System.Text;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Compaction;

namespace Athlon.Agent.Infrastructure;

public sealed class PreCompletionPipeline(
    AppSettings settings,
    TruncateArgsService truncateArgsService,
    IConversationCompactor conversationCompactor,
    IAppLogger logger) : IPreCompletionPipeline
{
    private readonly IAppLogger _logger = logger.ForContext("PreCompletionPipeline");

    public async Task<AgentSession> RunAsync(
        AgentSession session,
        PreCompletionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= PreCompletionOptions.Default;
        var cfg = settings.ContextCompaction;

        if (options.AllowTruncateArgs)
        {
            session = truncateArgsService.ApplyIfNeeded(session, cfg);
        }

        if (!options.AllowConversationCompact)
        {
            return session;
        }

        var kind = options.CompactionKind;

        var compactResult = await conversationCompactor.CompactIfNeededAsync(
            session,
            kind,
            options.ForceConversationCompact,
            options.EmitCompactionAudit,
            cancellationToken);

        if (compactResult.Compacted)
        {
            _logger.Information(
                "Conversation compact applied for session {SessionId}",
                session.Id);
        }

        return compactResult.Session;
    }
}

public sealed class ConversationCompactor(
    AppSettings settings,
    IAgentModelClient modelClient,
    IFileStorageService storage,
    IAppLogger logger) : IConversationCompactor
{
    private readonly IAppLogger _logger = logger.ForContext("ConversationCompactor");

    public async Task<ConversationCompactResult> CompactIfNeededAsync(
        AgentSession session,
        CompactionKind kind,
        bool force,
        bool emitAudit,
        CancellationToken cancellationToken = default)
    {
        var cfg = settings.ContextCompaction;
        var filtered = SummaryMessageBuilder.FilterSummaryMessages(session.Messages);
        if (filtered.Count == 0)
        {
            return new ConversationCompactResult(session, false);
        }

        var estimatedTokens = ContextTokenEstimator.Estimate(filtered);
        if (!ConversationCutoffPlanner.ShouldCompact(filtered, estimatedTokens, cfg, force))
        {
            return new ConversationCompactResult(session, false);
        }

        var cutoff = ConversationCutoffPlanner.DetermineCutoffIndex(filtered, estimatedTokens, cfg);
        cutoff = ConversationCutoffPlanner.FindSafeCutoffPoint(filtered, cutoff);
        if (cutoff <= 0)
        {
            return new ConversationCompactResult(session, false);
        }

        var prefix = filtered.Take(cutoff).ToList();
        var tail = filtered.Skip(cutoff).ToList();
        var originalCount = filtered.Count;
        var tokensBefore = estimatedTokens;

        string? transcriptPath = null;
        if (cfg.OffloadBeforeCompact)
        {
            transcriptPath = await storage.SaveTranscriptAsync(session.Id, session.Messages, cancellationToken);
        }

        var formatted = ConversationSummaryFormatter.FormatMessages(prefix);
        if (formatted.Length > cfg.MaxConversationCharsForSummary)
        {
            formatted = formatted[^cfg.MaxConversationCharsForSummary..];
        }

        var prompt = cfg.SummaryPrompt.Replace("{messages}", formatted, StringComparison.Ordinal);
        var summaryResponse = await modelClient.CompleteAsync(
            new AgentModelRequest(
                new[] { new AgentModelMessage("user", prompt) },
                Array.Empty<ToolDefinition>(),
                AllowToolCalls: false,
                MaxTokens: cfg.SummaryMaxTokens),
            cancellationToken: cancellationToken);

        var summary = summaryResponse.Content.Trim();
        if (string.IsNullOrWhiteSpace(summary))
        {
            summary = "(empty summary)";
        }

        var summaryMessage = SummaryMessageBuilder.CreateSummaryPlaceholder(summary, transcriptPath);
        var compactMessages = new List<ChatMessage>();

        var auditKind = kind == CompactionKind.ManualCompact
            ? CompactionKind.ManualCompact
            : CompactionKind.ConversationCompact;

        if (emitAudit)
        {
            var tokensAfterPreview = ContextTokenEstimator.Estimate(new[] { summaryMessage }.Concat(tail).ToArray());
            var auditContent = auditKind == CompactionKind.ManualCompact
                ? CompactionMessageContent.CreateManualCompact(
                    tokensBefore,
                    tokensAfterPreview,
                    originalCount,
                    transcriptPath ?? string.Empty,
                    summary)
                : CompactionMessageContent.CreateConversationCompact(
                    tokensBefore,
                    tokensAfterPreview,
                    originalCount,
                    transcriptPath,
                    summary);
            compactMessages.Add(CompactionMessageContent.CreateCompactionMessage(auditContent));
        }

        compactMessages.Add(summaryMessage);
        compactMessages.AddRange(tail);

        await storage.SaveContextSummaryAsync(
            new ContextSummary(
                Guid.NewGuid().ToString("N"),
                session.Id,
                summary,
                originalCount,
                DateTimeOffset.UtcNow),
            cancellationToken);

        session = session.WithMessages(compactMessages);

        _logger.Information(
            "Compacted session {SessionId} from {OriginalCount} to {ResultCount} messages (kind {Kind}, force {Force})",
            session.Id,
            originalCount,
            session.Messages.Count,
            auditKind,
            force);

        return new ConversationCompactResult(session, true);
    }
}

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

public sealed class CompressAgentTool : IAgentTool
{
    public ToolDefinition Definition { get; } = new(
        Core.CompressTool.ToolName,
        "Manually compress conversation context.",
        new Dictionary<string, string>(),
        RequiresApproval: false);

    public Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken cancellationToken = default) =>
        Task.FromResult(ToolResult.Success("Compressing..."));
}
