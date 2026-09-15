namespace Athlon.Agent.Core.Plan;

public sealed class PlanTurnOrchestrator(
    IAgentOrchestrator orchestrator,
    IPlanRunStore runStore,
    IPlanPhaseAccessor phaseAccessor,
    IPlanSessionState sessionState,
    IUserQuestionState userQuestions) : IPlanTurnOrchestrator
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

        var runId = IdGen.NewId();
        var run = new PlanRun
        {
            Id = runId,
            SessionId = session.Id,
            Goal = userInput.Trim(),
            Phase = PlanPhase.Explore,
            Status = PlanRunStatuses.Draft
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
                // The plan lives in memory only; re-read it so any markdown written by the last
                // publish_plan call wins over a stale copy on the run object.
                var published = await runStore.ReadPlanMarkdownAsync(session.Id, cancellationToken)
                    .ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(published))
                {
                    run.PlanMarkdown = published;
                    run.Title = PlanDocumentParser.ParseTitle(published) ?? run.Title ?? "Plan";
                    var parsedTodos = PlanDocumentParser.ParseTodos(published).ToList();
                    if (parsedTodos.Count > 0)
                    {
                        run.Todos = parsedTodos;
                    }
                }

                if (run.Todos.Count == 0 && !string.IsNullOrWhiteSpace(run.PlanMarkdown))
                {
                    run.Todos = PlanDocumentParser.ParseTodos(run.PlanMarkdown).ToList();
                }

                run.Status = PlanRunStatuses.Approved;
                run.Phase = PlanPhase.Done;
                // The revise conversation is over; a later Plan turn starts fresh.
                run.IsRevisionTurn = false;
                run.RevisionProducedNewPlan = null;
                run.UpdatedAt = DateTimeOffset.UtcNow;
                await PersistRunAsync(run, cancellationToken).ConfigureAwait(false);
                break;

            case PlanContinuationKind.Revise when run.Phase == PlanPhase.AwaitConfirm:
                run.Phase = PlanPhase.Draft;
                run.Status = PlanRunStatuses.Draft;
                run.UpdatedAt = DateTimeOffset.UtcNow;
                // Remember the pre-revision markdown so we can tell whether this turn actually
                // produced a new plan (revision is now multi-turn: an inconclusive turn must not
                // silently keep the old plan nor fabricate a placeholder).
                var markdownBeforeRevision = await runStore
                    .ReadPlanMarkdownAsync(session.Id, cancellationToken)
                    .ConfigureAwait(false)
                    ?? run.PlanMarkdown;
                run.PublishedThisTurn = false;
                run.RevisionProducedNewPlan = null;
                // Marks the transient revision state for the duration of the revise conversation
                // (including a clarification asked mid-revision), so returning here does not depend
                // on markdown presence, which an Explore turn would also satisfy.
                run.IsRevisionTurn = true;
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

                if (run.Phase == PlanPhase.AwaitClarify)
                {
                    // The model asked a follow-up question mid-revision. Park the run so the
                    // QuestionBar can resume it, exactly as in Explore.
                    run.Status = PlanRunStatuses.AwaitingClarification;
                    run.UpdatedAt = DateTimeOffset.UtcNow;
                    await PersistRunAsync(run, cancellationToken).ConfigureAwait(false);
                    break;
                }

                if (run.PublishedThisTurn)
                {
                    // A new plan is on the store; seal it back to AwaitConfirm so Build stays available.
                    var publishedMarkdown = await runStore
                        .ReadPlanMarkdownAsync(session.Id, cancellationToken)
                        .ConfigureAwait(false);
                    session = await SealToAwaitConfirmAsync(
                            session,
                            run,
                            string.IsNullOrWhiteSpace(publishedMarkdown)
                                ? run.PlanMarkdown ?? string.Empty
                                : publishedMarkdown,
                            cancellationToken)
                        .ConfigureAwait(false);

                    var sealedRun = phaseAccessor.GetActiveRun(session.Id) ?? run;
                    sealedRun.RevisionProducedNewPlan = true;
                    sealedRun.PublishedThisTurn = false;
                    sealedRun.IsRevisionTurn = false;
                    await PersistRunAsync(sealedRun, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    // Multi-turn revision: no publish_plan this turn. Keep the existing plan and go
                    // back to AwaitConfirm so the user can continue the discussion instead of being
                    // sealed into a fabricated plan. RevisionProducedNewPlan lets the UI say so.
                    run.Phase = PlanPhase.AwaitConfirm;
                    run.Status = PlanRunStatuses.AwaitingConfirmation;
                    run.PlanMarkdown = markdownBeforeRevision;
                    run.PublishedThisTurn = false;
                    run.RevisionProducedNewPlan = false;
                    // Stays true: the user is still inside the revise conversation, so a follow-up
                    // revise turn (or a clarification) must not be mistaken for a fresh Explore.
                    run.IsRevisionTurn = true;
                    run.UpdatedAt = DateTimeOffset.UtcNow;
                    await PersistRunAsync(run, cancellationToken).ConfigureAwait(false);
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
        userQuestions.Clear(session.Id);

        // A clarification asked during a revision belongs to that revision: return to AwaitConfirm
        // so the user can keep discussing the plan instead of falling back to Explore. The revise
        // turn sets IsRevisionTurn, which is the only reliable signal (a plain markdown check would
        // also match an Explore run that had already published).
        var wasRevising = run.IsRevisionTurn;
        run.PublishedThisTurn = false;
        run.Phase = wasRevising ? PlanPhase.Draft : PlanPhase.Explore;
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

        if (wasRevising)
        {
            return await FinalizeRevisionAsync(session, run, cancellationToken).ConfigureAwait(false);
        }

        return await FinalizeConsultingAsync(session, callbacks, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Wraps up a revision turn that was resumed from a clarification: seal a newly published plan,
    /// otherwise keep the previous one and tell the caller so the UI can say so.
    /// </summary>
    private async Task<AgentSession> FinalizeRevisionAsync(
        AgentSession session,
        PlanRun previous,
        CancellationToken cancellationToken)
    {
        var run = phaseAccessor.GetActiveRun(session.Id) ?? previous;
        if (run.Phase == PlanPhase.AwaitClarify)
        {
            run.Status = PlanRunStatuses.AwaitingClarification;
            run.UpdatedAt = DateTimeOffset.UtcNow;
            await PersistRunAsync(run, cancellationToken).ConfigureAwait(false);
            return session;
        }

        if (run.PublishedThisTurn)
        {
            var published = await runStore.ReadPlanMarkdownAsync(session.Id, cancellationToken)
                .ConfigureAwait(false);
            session = await SealToAwaitConfirmAsync(
                    session,
                    run,
                    string.IsNullOrWhiteSpace(published) ? run.PlanMarkdown ?? string.Empty : published,
                    cancellationToken)
                .ConfigureAwait(false);
            var sealedRun = phaseAccessor.GetActiveRun(session.Id) ?? run;
            sealedRun.RevisionProducedNewPlan = true;
            sealedRun.PublishedThisTurn = false;
            sealedRun.IsRevisionTurn = false;
            await PersistRunAsync(sealedRun, cancellationToken).ConfigureAwait(false);
            return session;
        }

        // Nothing new was published: preserve the plan the user was looking at.
        run.Phase = PlanPhase.AwaitConfirm;
        run.Status = PlanRunStatuses.AwaitingConfirmation;
        run.PlanMarkdown = previous.PlanMarkdown;
        run.PublishedThisTurn = false;
        run.RevisionProducedNewPlan = false;
        run.IsRevisionTurn = true;
        run.UpdatedAt = DateTimeOffset.UtcNow;
        await PersistRunAsync(run, cancellationToken).ConfigureAwait(false);
        return session;
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

        if (run.Phase == PlanPhase.AwaitClarify)
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
        run.Title = PlanDocumentParser.ParseTitle(markdown) ?? run.Title ?? "Plan";
        if (run.Todos.Count == 0)
        {
            run.Todos = PlanDocumentParser.ParseTodos(markdown).ToList();
        }

        userQuestions.Clear(session.Id);
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
