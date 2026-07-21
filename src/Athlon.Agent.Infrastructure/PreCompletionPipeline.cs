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
        var isManualCompact = options.Strategy == CompactionStrategy.ManualCompact;

        if (!options.AllowConversationCompact)
        {
            return session;
        }

        var cfg = settings.ContextCompaction;
        if (!cfg.Enabled && !options.ForceConversationCompact)
        {
            return session;
        }

        if (!cfg.DynamicCompaction.Enabled)
        {
            return await RunLegacyAsync(session, options, runtimeContext, cancellationToken);
        }

        if (runtimeContext is null)
        {
            return session;
        }

        var budget = runtimeContext.Budget;
        var rawHistoryEstimate = runtimeContext.RawHistoryEstimate;
        var conversation = ConversationMessageFilters.WithoutCompactionAudits(session.Messages);
        if (conversation.Count == 0)
        {
            return session;
        }

        var force = options.ForceConversationCompact || runtimeContext.ForceOverflow;
        var pressure = ContextPressureEvaluator.Evaluate(
            budget,
            cfg.DynamicCompaction,
            runtimeContext.ForceOverflow);

        if (!isManualCompact
            && !force
            && pressure == ContextPressureLevel.Normal
            && !ContextPressureEvaluator.MeetsStaticTruncateThreshold(conversation, cfg, rawHistoryEstimate)
            && budget.TotalUtilization < ContextPressureEvaluator.ResolveTruncateThreshold(cfg.DynamicCompaction))
        {
            return session;
        }

        var plan = DynamicCompactionPlan.Create(
            pressure,
            budget,
            conversation,
            cfg,
            force,
            rawHistoryEstimate);
        if (isManualCompact)
        {
            plan = plan with { ApplyConversationCompact = true };
        }

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
                conversation = ConversationMessageFilters.WithoutCompactionAudits(session.Messages);
                budget = ContextBudgetCalculator.RecomputeHistory(
                    budget,
                    session.Messages,
                    cfg,
                    runtimeContext.CalibrationMultiplier);
                rawHistoryEstimate = Math.Abs(runtimeContext.CalibrationMultiplier - 1.0) < 0.001
                    ? budget.EstimatedHistory
                    : ContextBudgetCalculator.EstimateRawHistory(session.Messages, cfg);
                runtimeContext = runtimeContext with
                {
                    Budget = budget,
                    RawHistoryEstimate = rawHistoryEstimate
                };
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
                conversation = ConversationMessageFilters.WithoutCompactionAudits(session.Messages);
                budget = ContextBudgetCalculator.RecomputeHistory(
                    budget,
                    session.Messages,
                    cfg,
                    runtimeContext.CalibrationMultiplier);
                rawHistoryEstimate = Math.Abs(runtimeContext.CalibrationMultiplier - 1.0) < 0.001
                    ? budget.EstimatedHistory
                    : ContextBudgetCalculator.EstimateRawHistory(session.Messages, cfg);
                runtimeContext = runtimeContext with
                {
                    Budget = budget,
                    RawHistoryEstimate = rawHistoryEstimate
                };
            }
        }

        if (!plan.ApplyConversationCompact && !isManualCompact)
        {
            return session;
        }

        if (isManualCompact)
        {
            plan = plan with { ApplyConversationCompact = true };
        }

        pressure = ContextPressureEvaluator.Evaluate(budget, cfg.DynamicCompaction, runtimeContext.ForceOverflow);
        plan = plan with { Pressure = pressure };

        var compactResult = await conversationCompactor.CompactIfNeededAsync(
            session,
            new CompactionExecutionRequest(
                options.CompactionKind,
                force,
                options.EmitCompactionAudit,
                options.Strategy,
                runtimeContext with { Budget = budget, RawHistoryEstimate = rawHistoryEstimate },
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
        CompactionRuntimeContext? runtimeContext,
        CancellationToken cancellationToken)
    {
        var cfg = settings.ContextCompaction;
        var conversation = ConversationMessageFilters.WithoutCompactionAudits(session.Messages);
        var truncateApplied = false;
        var reEvictApplied = false;

        if (conversation.Count > 0
            && ContextPressureEvaluator.MeetsStaticTruncateThreshold(conversation, cfg))
        {
            var truncatedMessages = truncateArgsService.ApplyToMessages(
                session.Messages,
                cfg,
                out truncateApplied);
            if (truncateApplied)
            {
                session = session with { Messages = truncatedMessages };
                conversation = ConversationMessageFilters.WithoutCompactionAudits(session.Messages);
            }

            var cutoff = ConversationCutoffPlanner.DetermineTruncateArgsCutoff(
                conversation,
                cfg.TruncateArgs,
                cfg.IncludeReasoningInModelContext);
            var (updatedMessages, changed) = PrefixToolResultReEvictor.Apply(
                session.Messages,
                cfg,
                cutoff);
            if (changed)
            {
                reEvictApplied = true;
                session = session with { Messages = updatedMessages };
            }
        }

        var compactResult = await conversationCompactor.CompactIfNeededAsync(
            session,
            new CompactionExecutionRequest(
                options.CompactionKind,
                options.ForceConversationCompact,
                options.EmitCompactionAudit,
                options.Strategy,
                runtimeContext,
                reEvictApplied
                    ? new DynamicCompactionPlan(
                        ContextPressureLevel.Normal,
                        ApplyTruncateArgs: truncateApplied,
                        ApplyPrefixReEvict: true,
                        ApplyConversationCompact: true,
                        KeepTokenBudget: 0,
                        MustPreserveAppendix: null)
                    : truncateApplied
                        ? new DynamicCompactionPlan(
                            ContextPressureLevel.Normal,
                            ApplyTruncateArgs: true,
                            ApplyPrefixReEvict: false,
                            ApplyConversationCompact: true,
                            KeepTokenBudget: 0,
                            MustPreserveAppendix: null)
                        : null),
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
