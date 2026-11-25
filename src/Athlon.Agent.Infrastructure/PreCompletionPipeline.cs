using Athlon.Agent.Core;
using Athlon.Agent.Core.Compaction;

namespace Athlon.Agent.Infrastructure;

/// <summary>
/// Turn-prep: truncate oversized tool args, then a single CONTEXT CHECKPOINT when utilization / force requires it.
/// </summary>
public sealed class PreCompletionPipeline(
    IConversationCompactor conversationCompactor,
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

        session = ApplyTruncateArgsIfNeeded(session, options, cfg, ref runtimeContext);

        var conversation = ConversationMessageFilters.WithoutCompactionAudits(session.Messages);
        if (conversation.Count == 0)
        {
            return session;
        }

        // ForceConversationCompact (overflow / manual) must still invoke the compactor even when
        // utilization is below the automatic threshold.
        var requireCompact = isManualCompact
            || options.ApplyEvenWhenBelowThreshold
            || options.ForceConversationCompact;
        var executionForce = options.ForceConversationCompact || runtimeContext?.ForceOverflow == true;
        ContextPressureLevel pressure;
        ContextBudgetSnapshot? budget = runtimeContext?.Budget;

        if (cfg.DynamicCompaction.Enabled && budget is { } dynamicBudget)
        {
            pressure = ContextPressureEvaluator.Evaluate(
                dynamicBudget,
                cfg.DynamicCompaction,
                runtimeContext!.ForceOverflow);

            if (!requireCompact
                && !executionForce
                && !ContextPressureEvaluator.ShouldCompact(
                    dynamicBudget,
                    conversation,
                    cfg,
                    pressure,
                    executionForce))
            {
                return session;
            }

            if (ContextPressureEvaluator.IsFixedOverheadDominating(dynamicBudget, cfg)
                && (executionForce || pressure is ContextPressureLevel.Critical or ContextPressureLevel.Overflow))
            {
                CompactionDecisionLog.Write(
                    _logger,
                    sink: null,
                    session.Id,
                    outcome: "skipped",
                    reason: "fixed_overhead_dominates",
                    pressure: pressure,
                    tokensBefore: dynamicBudget.EstimatedHistory);
            }

            var plan = DynamicCompactionPlan.Create(
                pressure,
                dynamicBudget,
                conversation,
                cfg,
                requireCompact || executionForce);
            if (isManualCompact)
            {
                plan = plan with { ApplyConversationCompact = true };
            }

            if (!plan.ApplyConversationCompact)
            {
                return session;
            }

            var compactResult = await conversationCompactor.CompactIfNeededAsync(
                session,
                new CompactionExecutionRequest(
                    options.CompactionKind,
                    executionForce,
                    options.EmitCompactionAudit,
                    options.Strategy,
                    runtimeContext,
                    plan),
                cancellationToken).ConfigureAwait(false);

            if (compactResult.Compacted)
            {
                _logger.Information(
                    "Checkpoint applied for session {SessionId} at pressure {Pressure}",
                    session.Id,
                    pressure);
            }

            return compactResult.Session;
        }

        // Static thresholds when dynamic compaction is off.
        var estimated = ContextTokenEstimator.Estimate(
            conversation,
            cfg.IncludeReasoningInModelContext,
            maxToolScreenshots: cfg.MaxToolScreenshotsInModelContext,
            hygiene: cfg.RequestHistoryHygiene);
        if (!requireCompact
            && !executionForce
            && !ConversationCutoffPlanner.ShouldCompact(conversation, estimated, cfg, executionForce))
        {
            return session;
        }

        pressure = executionForce ? ContextPressureLevel.Overflow : ContextPressureLevel.Critical;
        var staticPlan = new DynamicCompactionPlan(
            pressure,
            ApplyConversationCompact: true,
            CheckpointHistoryBuilder.ResolveRetainUserTokenBudget(cfg, pressure, executionForce));

        var result = await conversationCompactor.CompactIfNeededAsync(
            session,
            new CompactionExecutionRequest(
                options.CompactionKind,
                executionForce,
                options.EmitCompactionAudit,
                options.Strategy,
                runtimeContext,
                staticPlan),
            cancellationToken).ConfigureAwait(false);
        return result.Session;
    }

    private static AgentSession ApplyTruncateArgsIfNeeded(
        AgentSession session,
        PreCompletionOptions options,
        ContextCompactionSettings cfg,
        ref CompactionRuntimeContext? runtimeContext)
    {
        if (!options.AllowTruncateArgs || !cfg.TruncateArgs.Enabled)
        {
            return session;
        }

        var truncate = TruncateArgsService.Apply(session.Messages, cfg);
        if (!truncate.Changed)
        {
            return session;
        }

        session = session.WithMessages(truncate.Messages);
        if (runtimeContext is not { } ctx)
        {
            return session;
        }

        var budget = ContextBudgetCalculator.RecomputeHistory(
            ctx.Budget,
            session.Messages,
            cfg,
            ctx.CalibrationMultiplier);
        runtimeContext = ctx with { Budget = budget };
        return session;
    }
}