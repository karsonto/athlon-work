namespace Athlon.Agent.Core.Plan;

public sealed class PlanTurnOrchestrator(
    IAgentOrchestrator orchestrator,
    IPlanRunStore runStore,
    IPlanPhaseAccessor phaseAccessor,
    IPlanSessionState sessionState) : IPlanTurnOrchestrator
{
    public bool IsAwaitingUser(string sessionId)
    {
        var run = phaseAccessor.GetActiveRun(sessionId);
        return run is { IsAwaitingUser: true };
    }

    public async Task<AgentSession> RunUserTurnAsync(
        AgentSession session,
        string userInput,
        AgentTurnCallbacks? callbacks,
        CancellationToken cancellationToken)
    {
        var existing = phaseAccessor.GetActiveRun(session.Id)
            ?? await runStore.LoadActiveAsync(session.Id, cancellationToken).ConfigureAwait(false);
        if (existing is not null && existing.Phase != PlanPhase.Done)
        {
            if (existing.Phase == PlanPhase.AwaitClarify)
            {
                return await ResumeAfterClarificationAsync(
                    session,
                    existing,
                    userInput,
                    callbacks,
                    cancellationToken).ConfigureAwait(false);
            }

            if (existing.Phase == PlanPhase.AwaitConfirm)
            {
                return await ContinueAsync(
                    session,
                    PlanContinuationKind.Revise,
                    callbacks,
                    cancellationToken,
                    userInput).ConfigureAwait(false);
            }

            // Explore or leftover Draft: one consulting turn, then finalize (no auto-Draft).
            session = await RunPhaseAsync(
                    session,
                    existing,
                    userInput,
                    callbacks,
                    cancellationToken,
                    appendUserMessage: true)
                .ConfigureAwait(false);
            return await FinalizeConsultingAsync(session, callbacks, cancellationToken).ConfigureAwait(false);
        }

        var runId = Guid.NewGuid().ToString("N");
        var planPath = runStore.GetPlanMarkdownPath(session.Id);
        var run = new PlanRun
        {
            Id = runId,
            SessionId = session.Id,
            Goal = userInput.Trim(),
            Phase = PlanPhase.Explore,
            Status = PlanRunStatuses.Draft,
            PlanPath = planPath
        };

        await PersistRunAsync(run, cancellationToken).ConfigureAwait(false);

        session = await RunPhaseAsync(session, run, userInput, callbacks, cancellationToken, appendUserMessage: true)
            .ConfigureAwait(false);
        return await FinalizeConsultingAsync(session, callbacks, cancellationToken).ConfigureAwait(false);
    }

    public async Task<AgentSession> ContinueAsync(
        AgentSession session,
        PlanContinuationKind continuation,
        AgentTurnCallbacks? callbacks,
        CancellationToken cancellationToken,
        string? userInput = null)
    {
        var run = phaseAccessor.GetActiveRun(session.Id)
            ?? await runStore.LoadActiveAsync(session.Id, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("No active plan run for this session.");

        switch (continuation)
        {
            case PlanContinuationKind.Build when run.Phase == PlanPhase.AwaitConfirm:
                var disk = await runStore.ReadPlanMarkdownAsync(session.Id, cancellationToken)
                    .ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(disk))
                {
                    run.PlanMarkdown = disk;
                    run.Title = PlanDocumentParser.ParseTitle(disk) ?? run.Title ?? "Plan";
                    var parsedTodos = PlanDocumentParser.ParseTodos(disk).ToList();
                    if (parsedTodos.Count > 0)
                    {
                        run.Todos = parsedTodos;
                    }
                }
                else if (string.IsNullOrWhiteSpace(run.PlanMarkdown))
                {
                    run.PlanMarkdown = disk;
                }

                if (run.Todos.Count == 0 && !string.IsNullOrWhiteSpace(run.PlanMarkdown))
                {
                    run.Todos = PlanDocumentParser.ParseTodos(run.PlanMarkdown).ToList();
                }

                run.Status = PlanRunStatuses.Approved;
                run.Phase = PlanPhase.Done;
                run.UpdatedAt = DateTimeOffset.UtcNow;
                await PersistRunAsync(run, cancellationToken).ConfigureAwait(false);
                break;

            case PlanContinuationKind.Revise when run.Phase == PlanPhase.AwaitConfirm:
                run.Phase = PlanPhase.Draft;
                run.Status = PlanRunStatuses.Draft;
                run.UpdatedAt = DateTimeOffset.UtcNow;
                await PersistRunAsync(run, cancellationToken).ConfigureAwait(false);
                var revisionInput = userInput?.Trim() ?? string.Empty;
                session = await RunPhaseAsync(
                    session,
                    run,
                    revisionInput,
                    callbacks,
                    cancellationToken,
                    appendUserMessage: revisionInput.Length > 0).ConfigureAwait(false);
                run = phaseAccessor.GetActiveRun(session.Id)!;
                if (run.Phase == PlanPhase.Draft)
                {
                    session = await SealDraftAsync(session, run, callbacks, cancellationToken).ConfigureAwait(false);
                }

                break;

            default:
                throw new InvalidOperationException($"Invalid plan continuation {continuation} for phase {run.Phase}.");
        }

        return session;
    }

    private async Task<AgentSession> ResumeAfterClarificationAsync(
        AgentSession session,
        PlanRun run,
        string userInput,
        AgentTurnCallbacks? callbacks,
        CancellationToken cancellationToken)
    {
        run.PendingClarification = null;
        run.Phase = PlanPhase.Explore;
        run.Status = PlanRunStatuses.Draft;
        run.UpdatedAt = DateTimeOffset.UtcNow;
        await PersistRunAsync(run, cancellationToken).ConfigureAwait(false);

        session = await RunPhaseAsync(
            session,
            run,
            userInput,
            callbacks,
            cancellationToken,
            appendUserMessage: true).ConfigureAwait(false);
        return await FinalizeConsultingAsync(session, callbacks, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// After an Explore (or leftover Draft) consulting turn: pause on clarification,
    /// seal when publish_plan wrote a complete plan, otherwise stay in Explore.
    /// </summary>
    private async Task<AgentSession> FinalizeConsultingAsync(
        AgentSession session,
        AgentTurnCallbacks? callbacks,
        CancellationToken cancellationToken)
    {
        var run = phaseAccessor.GetActiveRun(session.Id);
        if (run is null)
        {
            return session;
        }

        if (run.PendingClarification is not null || run.Phase == PlanPhase.AwaitClarify)
        {
            run.Phase = PlanPhase.AwaitClarify;
            run.Status = PlanRunStatuses.AwaitingClarification;
            run.UpdatedAt = DateTimeOffset.UtcNow;
            await PersistRunAsync(run, cancellationToken).ConfigureAwait(false);
            return session;
        }

        if (run.Phase is PlanPhase.AwaitConfirm or PlanPhase.Done)
        {
            return session;
        }

        var markdown = await runStore.ReadPlanMarkdownAsync(session.Id, cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(markdown) && PlanDocumentParser.LooksComplete(markdown))
        {
            return await SealToAwaitConfirmAsync(session, run, markdown, cancellationToken).ConfigureAwait(false);
        }

        // Stay in Explore so the model can ask again or publish on a later user turn.
        if (run.Phase != PlanPhase.Explore)
        {
            run.Phase = PlanPhase.Explore;
            run.Status = PlanRunStatuses.Draft;
            run.UpdatedAt = DateTimeOffset.UtcNow;
            await PersistRunAsync(run, cancellationToken).ConfigureAwait(false);
        }

        return session;
    }

    private async Task<AgentSession> SealToAwaitConfirmAsync(
        AgentSession session,
        PlanRun run,
        string markdown,
        CancellationToken cancellationToken)
    {
        run.PlanMarkdown = markdown;
        run.PlanPath = runStore.GetPlanMarkdownPath(session.Id);
        run.Title = PlanDocumentParser.ParseTitle(markdown) ?? run.Title ?? "Plan";
        if (run.Todos.Count == 0)
        {
            run.Todos = PlanDocumentParser.ParseTodos(markdown).ToList();
        }

        run.PendingClarification = null;
        run.Status = PlanRunStatuses.AwaitingConfirmation;
        run.Phase = PlanPhase.AwaitConfirm;
        run.UpdatedAt = DateTimeOffset.UtcNow;
        await PersistRunAsync(run, cancellationToken).ConfigureAwait(false);
        return session;
    }

    private async Task<AgentSession> SealDraftAsync(
        AgentSession session,
        PlanRun run,
        AgentTurnCallbacks? callbacks,
        CancellationToken cancellationToken)
    {
        // Prefer content written by publish_plan during the Draft (revise) turn.
        var markdown = await runStore.ReadPlanMarkdownAsync(session.Id, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(markdown) || !PlanDocumentParser.LooksComplete(markdown))
        {
            // One retry Draft turn if publish_plan was missing/incomplete.
            session = await RunPhaseAsync(
                session,
                run,
                string.Empty,
                callbacks,
                cancellationToken,
                appendUserMessage: false).ConfigureAwait(false);
            run = phaseAccessor.GetActiveRun(session.Id) ?? run;
            markdown = await runStore.ReadPlanMarkdownAsync(session.Id, cancellationToken).ConfigureAwait(false);
        }

        if (string.IsNullOrWhiteSpace(markdown) || !PlanDocumentParser.LooksComplete(markdown))
        {
            var assistant = PlanDocumentParser.GetLastAssistantText(session);
            markdown = PlanDocumentParser.FallbackMarkdownFromAssistant(assistant, run.Goal);
            await runStore.WritePlanMarkdownAsync(session.Id, markdown, cancellationToken).ConfigureAwait(false);
        }

        return await SealToAwaitConfirmAsync(session, run, markdown, cancellationToken).ConfigureAwait(false);
    }

    private async Task<AgentSession> RunPhaseAsync(
        AgentSession session,
        PlanRun run,
        string userInput,
        AgentTurnCallbacks? callbacks,
        CancellationToken cancellationToken,
        bool appendUserMessage)
    {
        phaseAccessor.SetActiveRun(run);
        sessionState.NotifyChanged(run);
        return await orchestrator.SendAsync(
            session,
            userInput,
            null,
            callbacks,
            cancellationToken,
            computerUseActive: false,
            appendUserMessage: appendUserMessage).ConfigureAwait(false);
    }

    private async Task PersistRunAsync(PlanRun run, CancellationToken cancellationToken)
    {
        phaseAccessor.SetActiveRun(run);
        sessionState.NotifyChanged(run);
        await runStore.SaveActiveAsync(run, cancellationToken).ConfigureAwait(false);
    }
}
