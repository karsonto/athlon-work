using Athlon.Agent.Core;
using Athlon.Agent.Core.Compaction;
using Athlon.Agent.Infrastructure.Compaction;

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
        if (!cfg.Enabled && !options.ForceConversationCompact)
        {
            return session;
        }

        if (!cfg.DynamicCompaction.Enabled || runtimeContext is null)
        {
            return await RunLegacyAsync(session, options, cancellationToken);
        }

        var budget = runtimeContext.Budget;
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

        if (!force
            && pressure == ContextPressureLevel.Normal
            && !ContextPressureEvaluator.MeetsStaticTruncateThreshold(conversation, cfg)
            && budget.TotalUtilization < ContextPressureEvaluator.ResolveTruncateThreshold(cfg.DynamicCompaction))
        {
            return session;
        }

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
                conversation = ConversationMessageFilters.WithoutCompactionAudits(session.Messages);
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
                conversation = ConversationMessageFilters.WithoutCompactionAudits(session.Messages);
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
}
