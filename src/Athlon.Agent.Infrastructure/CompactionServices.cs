using System.Text;

using Athlon.Agent.Core;

using Athlon.Agent.Core.Compaction;



namespace Athlon.Agent.Infrastructure;



public sealed class PreCompletionPipeline(

    IConversationCompactor conversationCompactor,

    TruncateArgsService truncateArgsService,

    AppSettings settings,

    IAppLogger logger) : IPreCompletionPipeline

{

    private readonly IAppLogger _logger = logger.ForContext("PreCompletionPipeline");



    public async Task<AgentSession> RunAsync(

        AgentSession session,

        PreCompletionOptions? options = null,

        CompactionRuntimeContext? runtimeContext = null,

        CancellationToken cancellationToken = default)

    {

        options ??= PreCompletionOptions.Default;



        if (!options.AllowConversationCompact)

        {

            return session;

        }



        var cfg = settings.ContextCompaction;

        if (!cfg.DynamicCompaction.Enabled || runtimeContext is null)

        {

            return await RunLegacyAsync(session, options, cancellationToken);

        }



        var budget = runtimeContext.Budget;

        var conversation = GetConversationMessages(session.Messages);

        if (conversation.Count == 0)

        {

            return session;

        }



        var force = options.ForceConversationCompact || runtimeContext.ForceOverflow;

        var pressure = ContextPressureEvaluator.Evaluate(

            budget,

            cfg.DynamicCompaction,

            runtimeContext.ForceOverflow);

        var plan = DynamicCompactionPlan.Create(pressure, budget, conversation, cfg, force);



        var truncateApplied = false;

        var reEvictApplied = false;



        if (plan.ApplyTruncateArgs && options.AllowTruncateArgs)

        {

            var truncatedConversation = truncateArgsService.ApplyToMessages(

                session.Messages,

                cfg,

                out truncateApplied,

                plan.KeepTokenBudget);

            if (truncateApplied)

            {

                session = session with { Messages = truncatedConversation };

                conversation = GetConversationMessages(session.Messages);

                budget = ContextBudgetCalculator.RecomputeHistory(

                    budget,

                    session.Messages,

                    cfg,

                    runtimeContext.CalibrationMultiplier);

            }

        }



        if (plan.ApplyPrefixReEvict)

        {

            var prefixCutoff = ConversationCutoffPlanner.DetermineTruncateArgsCutoffFromKeepBudget(

                conversation,

                plan.KeepTokenBudget,

                cfg.IncludeReasoningInModelContext);

            var (updatedMessages, changed) = PrefixToolResultReEvictor.Apply(

                session.Messages,

                cfg,

                prefixCutoff);

            if (changed)

            {

                reEvictApplied = true;

                session = session with { Messages = updatedMessages };

                conversation = GetConversationMessages(session.Messages);

                budget = ContextBudgetCalculator.RecomputeHistory(

                    budget,

                    session.Messages,

                    cfg,

                    runtimeContext.CalibrationMultiplier);

            }

        }



        if (!plan.ApplyConversationCompact)

        {

            return session;

        }



        pressure = ContextPressureEvaluator.Evaluate(budget, cfg.DynamicCompaction, runtimeContext.ForceOverflow);

        plan = plan with { Pressure = pressure };



        var compactResult = await conversationCompactor.CompactIfNeededAsync(

            session,

            new CompactionExecutionRequest(

                options.CompactionKind,

                force,

                options.EmitCompactionAudit,

                runtimeContext with { Budget = budget },

                plan with

                {

                    ApplyTruncateArgs = truncateApplied,

                    ApplyPrefixReEvict = reEvictApplied

                }),

            cancellationToken);



        if (compactResult.Compacted)

        {

            _logger.Information(

                "Dynamic compaction applied for session {SessionId} at pressure {Pressure} (utilization {Utilization:P0})",

                session.Id,

                plan.Pressure,

                budget.TotalUtilization);

        }



        return compactResult.Session;

    }



    private async Task<AgentSession> RunLegacyAsync(

        AgentSession session,

        PreCompletionOptions options,

        CancellationToken cancellationToken)

    {

        var compactResult = await conversationCompactor.CompactIfNeededAsync(

            session,

            new CompactionExecutionRequest(

                options.CompactionKind,

                options.ForceConversationCompact,

                options.EmitCompactionAudit),

            cancellationToken);



        if (compactResult.Compacted)

        {

            _logger.Information(

                "Conversation compact applied for session {SessionId}",

                session.Id);

        }



        return compactResult.Session;

    }



    private static List<ChatMessage> GetConversationMessages(IReadOnlyList<ChatMessage> messages) =>

        messages.Where(message => message.Role != MessageRole.Compaction).ToList();

}



public sealed class ConversationCompactor(

    AppSettings settings,

    IAgentModelClient modelClient,

    IFileStorageService storage,

    TruncateArgsService truncateArgsService,

    IAppLogger logger) : IConversationCompactor

{

    private readonly IAppLogger _logger = logger.ForContext("ConversationCompactor");



    public async Task<ConversationCompactResult> CompactIfNeededAsync(

        AgentSession session,

        CompactionExecutionRequest request,

        CancellationToken cancellationToken = default)

    {

        var cfg = settings.ContextCompaction;

        var conversation = GetConversationMessages(session.Messages);

        if (conversation.Count == 0)

        {

            return new ConversationCompactResult(session, false);

        }



        var truncateArgsApplied = false;

        if (!cfg.DynamicCompaction.Enabled || request.RuntimeContext is null)

        {

            conversation = truncateArgsService

                .ApplyToMessages(conversation, cfg, out truncateArgsApplied)

                .Where(message => message.Role != MessageRole.Compaction)

                .ToList();

        }

        else if (request.Plan?.ApplyTruncateArgs == true)

        {

            truncateArgsApplied = true;

        }



        var estimatedTokens = ContextTokenEstimator.Estimate(conversation, cfg.IncludeReasoningInModelContext);

        var shouldCompact = request.RuntimeContext is { } runtime

            ? ContextPressureEvaluator.ShouldCompact(

                runtime.Budget,

                conversation,

                cfg,

                request.Plan?.Pressure ?? ContextPressureLevel.Normal,

                request.Force)

            : ConversationCutoffPlanner.ShouldCompact(conversation, estimatedTokens, cfg, request.Force);



        if (!shouldCompact)

        {

            return new ConversationCompactResult(session, false);

        }



        var keepTokenBudget = request.Plan?.KeepTokenBudget;

        var cutoff = ConversationCutoffPlanner.DetermineCutoffIndex(

            conversation,

            estimatedTokens,

            cfg,

            keepTokenBudget);

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



        var formatted = ConversationSummaryFormatter.FormatMessages(prefix);

        if (formatted.Length > cfg.MaxConversationCharsForSummary)

        {

            formatted = formatted[^cfg.MaxConversationCharsForSummary..];

        }



        var mustPreserve = request.Plan?.MustPreserveAppendix;

        var promptBody = BuildSummaryPrompt(cfg.SummaryPrompt, formatted, mustPreserve);

        string summary;

        try

        {

            var summaryResponse = await modelClient.CompleteAsync(

                new AgentModelRequest(

                    new[] { new AgentModelMessage("user", promptBody) },

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



        var summaryMessage = SummaryMessageBuilder.CreateSummaryPlaceholder(summary, transcriptPath);

        var compactMessages = new List<ChatMessage>();



        var auditKind = request.Kind == CompactionKind.ManualCompact

            ? CompactionKind.ManualCompact

            : CompactionKind.ConversationCompact;

        var strategy = request.Force

            ? CompactionStrategy.ForceCompact

            : request.Kind == CompactionKind.ManualCompact

                ? CompactionStrategy.ManualCompact

                : CompactionStrategy.ConversationCompact;

        var layers = new List<CompactionLayer> { CompactionLayer.ConversationCompact };

        if (truncateArgsApplied)

        {

            layers.Insert(0, CompactionLayer.TruncateArgs);

        }



        if (request.Plan?.ApplyPrefixReEvict == true)

        {

            layers.Insert(0, CompactionLayer.ToolResultEviction);

        }



        var pressure = request.Plan?.Pressure;

        var utilization = request.RuntimeContext?.Budget.TotalUtilization;



        if (request.EmitAudit)

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

                    layers,

                    pressure,

                    utilization)

                : CompactionMessageContent.CreateConversationCompact(

                    tokensBefore,

                    tokensAfterPreview,

                    originalCount,

                    transcriptPath,

                    summary,

                    strategy,

                    layers,

                    pressure,

                    utilization);

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

            request.Force);



        return new ConversationCompactResult(session, true);

    }



    private static string BuildSummaryPrompt(string template, string formattedMessages, string? mustPreserveAppendix)

    {

        var mustPreserve = string.IsNullOrWhiteSpace(mustPreserveAppendix) ? string.Empty : mustPreserveAppendix.Trim();

        return template

            .Replace("{must_preserve}", mustPreserve, StringComparison.Ordinal)

            .Replace("{messages}", formattedMessages, StringComparison.Ordinal);

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


