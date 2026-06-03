using System.Text;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Compaction;
using Athlon.Agent.Core.Plan;

namespace Athlon.Agent.Infrastructure;

public sealed class PreCompletionPipeline(
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

        if (!options.AllowConversationCompact)
        {
            return session;
        }

        var compactResult = await conversationCompactor.CompactIfNeededAsync(
            session,
            options.CompactionKind,
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
    TruncateArgsService truncateArgsService,
    IPlanNotebook planNotebook,
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
        var conversation = GetConversationMessages(session.Messages);
        if (conversation.Count == 0)
        {
            return new ConversationCompactResult(session, false);
        }

        conversation = truncateArgsService
            .ApplyToMessages(conversation, cfg, out var truncateArgsApplied)
            .Where(message => message.Role != MessageRole.Compaction)
            .ToList();

        var estimatedTokens = ContextTokenEstimator.Estimate(conversation, cfg.IncludeReasoningInModelContext);
        if (!ConversationCutoffPlanner.ShouldCompact(conversation, estimatedTokens, cfg, force))
        {
            return new ConversationCompactResult(session, false);
        }

        var cutoff = ConversationCutoffPlanner.DetermineCutoffIndex(conversation, estimatedTokens, cfg);
        if (cutoff <= 0)
        {
            _logger.Debug("Compaction triggered but safe cutoff is 0 — skipping");
            return new ConversationCompactResult(session, false);
        }

        var prefix = SummaryMessageBuilder.FilterSummaryMessages(conversation.Take(cutoff).ToList());
        var tail = conversation.Skip(cutoff).ToList();
        var originalCount = conversation.Count;
        var tokensBefore = estimatedTokens;

        string? transcriptPath = null;
        if (cfg.OffloadBeforeCompact)
        {
            transcriptPath = await storage.SaveTranscriptAsync(session.Id, session.Messages, cancellationToken);
        }

        var plan = planNotebook.GetCurrent(session.Id);
        var includePlan = plan is { Phase: PlanPhase.Approved };
        var formatted = ConversationSummaryFormatter.FormatMessages(prefix);
        if (formatted.Length > cfg.MaxConversationCharsForSummary)
        {
            formatted = formatted[^cfg.MaxConversationCharsForSummary..];
        }

        var planAppendix = includePlan
            ? CompactionPlanContextBuilder.BuildSummaryPromptAppendix(plan)
            : null;
        var promptBody = string.IsNullOrWhiteSpace(planAppendix)
            ? formatted
            : planAppendix + "\n\n<conversation_history>\n" + formatted + "\n</conversation_history>";

        var prompt = cfg.SummaryPrompt.Replace("{messages}", promptBody, StringComparison.Ordinal);
        string summary;
        try
        {
            var summaryResponse = await modelClient.CompleteAsync(
                new AgentModelRequest(
                    new[] { new AgentModelMessage("user", prompt) },
                    Array.Empty<ToolDefinition>(),
                    AllowToolCalls: false,
                    MaxTokens: cfg.SummaryMaxTokens),
                cancellationToken: cancellationToken);

            summary = summaryResponse.Content.Trim();
            if (string.IsNullOrWhiteSpace(summary))
            {
                summary = "(Summary unavailable)";
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Summarization LLM call failed for session {SessionId}", session.Id);
            summary = "(Summarization failed: " + ex.Message + ")";
        }

        summary = includePlan
            ? CompactionPlanContextBuilder.EnrichSummaryText(summary, plan)
            : summary;
        var summaryMessage = SummaryMessageBuilder.CreateSummaryPlaceholder(summary, transcriptPath);
        var compactMessages = new List<ChatMessage>();

        var auditKind = kind == CompactionKind.ManualCompact
            ? CompactionKind.ManualCompact
            : CompactionKind.ConversationCompact;
        var strategy = force
            ? CompactionStrategy.ForceCompact
            : kind == CompactionKind.ManualCompact
                ? CompactionStrategy.ManualCompact
                : CompactionStrategy.ConversationCompact;
        var layers = new List<CompactionLayer> { CompactionLayer.ConversationCompact };
        if (truncateArgsApplied)
        {
            layers.Insert(0, CompactionLayer.TruncateArgs);
        }

        if (emitAudit)
        {
            var tokensAfterPreview = ContextTokenEstimator.Estimate(
                new[] { summaryMessage }.Concat(tail).ToArray(),
                cfg.IncludeReasoningInModelContext);
            var auditContent = auditKind == CompactionKind.ManualCompact
                ? CompactionMessageContent.CreateManualCompact(
                    tokensBefore,
                    tokensAfterPreview,
                    originalCount,
                    transcriptPath ?? string.Empty,
                    summary,
                    layers)
                : CompactionMessageContent.CreateConversationCompact(
                    tokensBefore,
                    tokensAfterPreview,
                    originalCount,
                    transcriptPath,
                    summary,
                    strategy,
                    layers);
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

    private static List<ChatMessage> GetConversationMessages(IReadOnlyList<ChatMessage> messages) =>
        messages.Where(message => message.Role != MessageRole.Compaction).ToList();
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
