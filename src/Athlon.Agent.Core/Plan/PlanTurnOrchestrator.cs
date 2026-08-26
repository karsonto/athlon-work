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
            // Follow-up while a run is active (including AwaitConfirm Q&A): stay on phase.
            return await RunPhaseAsync(session, existing, userInput, callbacks, cancellationToken, appendUserMessage: true)
                .ConfigureAwait(false);
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
        run = phaseAccessor.GetActiveRun(session.Id)!;

        if (run.Phase == PlanPhase.Explore)
        {
            run.Phase = PlanPhase.Draft;
            await PersistRunAsync(run, cancellationToken).ConfigureAwait(false);
            session = await RunPhaseAsync(
                session,
                run,
                string.Empty,
                callbacks,
                cancellationToken,
                appendUserMessage: false).ConfigureAwait(false);
            run = phaseAccessor.GetActiveRun(session.Id)!;
        }

        if (run.Phase == PlanPhase.Draft)
        {
            session = await SealDraftAsync(session, run, callbacks, cancellationToken).ConfigureAwait(false);
        }

        return session;
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
                run.Status = PlanRunStatuses.Approved;
                run.Phase = PlanPhase.Done;
                run.UpdatedAt = DateTimeOffset.UtcNow;
                if (string.IsNullOrWhiteSpace(run.PlanMarkdown))
                {
                    run.PlanMarkdown = await runStore.ReadPlanMarkdownAsync(session.Id, cancellationToken)
                        .ConfigureAwait(false);
                }

                if (run.Todos.Count == 0 && !string.IsNullOrWhiteSpace(run.PlanMarkdown))
                {
                    run.Todos = PlanDocumentParser.ParseTodos(run.PlanMarkdown).ToList();
                }

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

    private async Task<AgentSession> SealDraftAsync(
        AgentSession session,
        PlanRun run,
        AgentTurnCallbacks? callbacks,
        CancellationToken cancellationToken)
    {
        // Prefer content written by publish_plan during the Draft turn.
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

        run.PlanMarkdown = markdown;
        run.PlanPath = runStore.GetPlanMarkdownPath(session.Id);
        run.Title = PlanDocumentParser.ParseTitle(markdown) ?? run.Title ?? "Plan";
        run.Todos = PlanDocumentParser.ParseTodos(markdown).ToList();
        run.Status = PlanRunStatuses.AwaitingConfirmation;
        run.Phase = PlanPhase.AwaitConfirm;
        run.UpdatedAt = DateTimeOffset.UtcNow;
        await PersistRunAsync(run, cancellationToken).ConfigureAwait(false);
        return session;
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
