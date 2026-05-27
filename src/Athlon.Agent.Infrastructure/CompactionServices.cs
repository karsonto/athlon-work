using System.Text.Json;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Compaction;

namespace Athlon.Agent.Infrastructure;

public sealed class PreCompletionPipeline(
    AppSettings settings,
    MicrocompactService microcompactService,
    IAutoCompactService autoCompactService,
    IAppLogger logger) : IPreCompletionPipeline
{
    private readonly IAppLogger _logger = logger.ForContext("PreCompletionPipeline");

    public async Task<AgentSession> RunAsync(AgentSession session, CancellationToken cancellationToken = default)
    {
        var cfg = settings.ContextCompaction;
        var messages = session.Messages.ToList();
        var tokens = ContextTokenEstimator.Estimate(messages);

        var keep = tokens >= cfg.MicrocompactAggressiveTokenThreshold
            ? cfg.MicrocompactKeepToolMessagesAggressive
            : cfg.MicrocompactKeepToolMessages;

        microcompactService.Apply(messages, keep, cfg.MicrocompactMinContentLength);
        session = session.WithMessages(messages);

        tokens = ContextTokenEstimator.Estimate(session.Messages);
        if (tokens < cfg.AutoCompactTokenThreshold)
        {
            return session;
        }

        _logger.Information(
            "Auto-compacting session {SessionId} at estimated {EstimatedTokens} tokens (threshold {Threshold})",
            session.Id,
            tokens,
            cfg.AutoCompactTokenThreshold);

        return await autoCompactService.CompactAsync(session, cancellationToken);
    }
}

public sealed class AutoCompactService(
    AppSettings settings,
    IAgentModelClient modelClient,
    IFileStorageService storage,
    IAppLogger logger) : IAutoCompactService
{
    private readonly IAppLogger _logger = logger.ForContext("AutoCompactService");

    public async Task<AgentSession> CompactAsync(AgentSession session, CancellationToken cancellationToken = default)
    {
        var cfg = settings.ContextCompaction;
        var originalCount = session.Messages.Count;
        var transcriptPath = await storage.SaveTranscriptAsync(session.Id, session.Messages, cancellationToken);

        var conversationJson = JsonSerializer.Serialize(session.Messages);
        if (conversationJson.Length > cfg.MaxConversationCharsForSummary)
        {
            conversationJson = conversationJson[^cfg.MaxConversationCharsForSummary..];
        }

        var summaryRequest = new AgentModelRequest(
            new[]
            {
                new AgentModelMessage("user", "Summarize for continuity:\n" + conversationJson)
            },
            Array.Empty<ToolDefinition>(),
            AllowToolCalls: false,
            MaxTokens: cfg.SummaryMaxTokens);

        var summaryResponse = await modelClient.CompleteAsync(summaryRequest, cancellationToken);
        var summary = summaryResponse.Content.Trim();
        if (string.IsNullOrWhiteSpace(summary))
        {
            summary = "(empty summary)";
        }

        var compressedContent = $"[Compressed. Transcript: {transcriptPath}]\n{summary}";
        var compressedMessage = ChatMessage.Create(MessageRole.User, compressedContent);
        session = session.WithMessages(new[] { compressedMessage });

        var contextSummary = new ContextSummary(
            Guid.NewGuid().ToString("N"),
            session.Id,
            summary,
            originalCount,
            DateTimeOffset.UtcNow);

        await storage.SaveContextSummaryAsync(contextSummary, cancellationToken);
        await storage.SaveSessionAsync(session, cancellationToken);

        var afterTokens = ContextTokenEstimator.Estimate(session.Messages);
        if (afterTokens >= cfg.MicrocompactAggressiveTokenThreshold)
        {
            _logger.Warning(
                "Session {SessionId} still has high estimated tokens ({EstimatedTokens}) after compact",
                session.Id,
                afterTokens);
        }

        _logger.Information(
            "Compacted session {SessionId} from {OriginalCount} messages to 1 (transcript {TranscriptPath})",
            session.Id,
            originalCount,
            transcriptPath);

        return session;
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
