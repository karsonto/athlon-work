namespace Athlon.Agent.Core.Debug;

public sealed class DebugTurnOrchestrator(
    IAgentOrchestrator orchestrator,
    IDebugRunStore runStore,
    IDebugPhaseAccessor phaseAccessor,
    IDebugSessionState sessionState) : IDebugTurnOrchestrator
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
        if (existing is not null && existing.Phase != DebugPhase.Done)
        {
            return await RunPhaseAsync(session, existing, userInput, callbacks, cancellationToken, appendUserMessage: true)
                .ConfigureAwait(false);
        }

        var runId = Guid.NewGuid().ToString("N");
        var run = new DebugRun
        {
            Id = runId,
            SessionId = session.Id,
            LogPath = runStore.CreateLogPath(runId),
            BugDescription = userInput.Trim(),
            Phase = DebugPhase.Hypothesize
        };

        await PersistRunAsync(run, cancellationToken).ConfigureAwait(false);

        session = await RunPhaseAsync(session, run, userInput, callbacks, cancellationToken, appendUserMessage: true)
            .ConfigureAwait(false);
        run = phaseAccessor.GetActiveRun(session.Id)!;

        if (run.Phase == DebugPhase.Hypothesize)
        {
            var text = DebugRunParser.GetLastAssistantText(session);
            var hypotheses = DebugRunParser.ParseHypotheses(text);
            if (hypotheses.Count == 0)
            {
                session = await RunPhaseAsync(
                    session,
                    run,
                    string.Empty,
                    callbacks,
                    cancellationToken,
                    appendUserMessage: false).ConfigureAwait(false);
                run = phaseAccessor.GetActiveRun(session.Id)!;
                text = DebugRunParser.GetLastAssistantText(session);
                hypotheses = DebugRunParser.ParseHypothesesOrFallback(text);
            }

            run.Hypotheses = hypotheses.ToList();
            run.Phase = DebugPhase.Instrument;
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

        if (run.Phase == DebugPhase.Instrument)
        {
            var text = DebugRunParser.GetLastAssistantText(session);
            run.ReproStepsMarkdown = DebugRunParser.ParseReproSteps(text) ?? text;
            run.Phase = DebugPhase.AwaitRepro;
            await PersistRunAsync(run, cancellationToken).ConfigureAwait(false);
        }

        return session;
    }

    public async Task<AgentSession> ContinueAsync(
        AgentSession session,
        DebugContinuationKind continuation,
        AgentTurnCallbacks? callbacks,
        CancellationToken cancellationToken)
    {
        var run = phaseAccessor.GetActiveRun(session.Id)
            ?? await runStore.LoadActiveAsync(session.Id, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("No active debug run for this session.");

        switch (continuation)
        {
            case DebugContinuationKind.Reproduced when run.Phase == DebugPhase.AwaitRepro:
                session = await RunAnalyzeToConfirmAsync(session, run, callbacks, cancellationToken)
                    .ConfigureAwait(false);
                break;

            case DebugContinuationKind.StartFix when run.Phase == DebugPhase.AwaitFixConfirm:
                run.Phase = DebugPhase.Fix;
                await PersistRunAsync(run, cancellationToken).ConfigureAwait(false);
                session = await RunPhaseAsync(
                    session,
                    run,
                    string.Empty,
                    callbacks,
                    cancellationToken,
                    appendUserMessage: false).ConfigureAwait(false);
                run = phaseAccessor.GetActiveRun(session.Id)!;
                run.Phase = DebugPhase.AwaitVerify;
                await PersistRunAsync(run, cancellationToken).ConfigureAwait(false);
                break;

            case DebugContinuationKind.Reanalyze when run.Phase == DebugPhase.AwaitFixConfirm:
                session = await RunAnalyzeToConfirmAsync(session, run, callbacks, cancellationToken)
                    .ConfigureAwait(false);
                break;

            case DebugContinuationKind.VerifiedFixed when run.Phase == DebugPhase.AwaitVerify:
                session = await RunCleanupToDoneAsync(session, run, callbacks, cancellationToken)
                    .ConfigureAwait(false);
                break;

            case DebugContinuationKind.VerifiedNotFixed when run.Phase == DebugPhase.AwaitVerify:
                run.Phase = DebugPhase.Hypothesize;
                await PersistRunAsync(run, cancellationToken).ConfigureAwait(false);
                session = await RunPhaseAsync(
                    session,
                    run,
                    string.Empty,
                    callbacks,
                    cancellationToken,
                    appendUserMessage: false).ConfigureAwait(false);
                run = phaseAccessor.GetActiveRun(session.Id)!;
                run.Phase = DebugPhase.AwaitRepro;
                await PersistRunAsync(run, cancellationToken).ConfigureAwait(false);
                break;

            default:
                throw new InvalidOperationException($"Invalid debug continuation {continuation} for phase {run.Phase}.");
        }

        return session;
    }

    private async Task<AgentSession> RunAnalyzeToConfirmAsync(
        AgentSession session,
        DebugRun run,
        AgentTurnCallbacks? callbacks,
        CancellationToken cancellationToken)
    {
        run.Phase = DebugPhase.Analyze;
        await PersistRunAsync(run, cancellationToken).ConfigureAwait(false);
        session = await RunPhaseAsync(
            session,
            run,
            string.Empty,
            callbacks,
            cancellationToken,
            appendUserMessage: false).ConfigureAwait(false);
        run = phaseAccessor.GetActiveRun(session.Id)!;
        run.RootCauseSummary = DebugRunParser.GetLastAssistantText(session);
        run.Phase = DebugPhase.AwaitFixConfirm;
        await PersistRunAsync(run, cancellationToken).ConfigureAwait(false);
        return session;
    }

    private async Task<AgentSession> RunCleanupToDoneAsync(
        AgentSession session,
        DebugRun run,
        AgentTurnCallbacks? callbacks,
        CancellationToken cancellationToken)
    {
        run.Phase = DebugPhase.Cleanup;
        await PersistRunAsync(run, cancellationToken).ConfigureAwait(false);
        session = await RunPhaseAsync(
            session,
            run,
            string.Empty,
            callbacks,
            cancellationToken,
            appendUserMessage: false).ConfigureAwait(false);
        run = phaseAccessor.GetActiveRun(session.Id)!;
        run.Phase = DebugPhase.Done;
        await PersistRunAsync(run, cancellationToken).ConfigureAwait(false);
        return session;
    }

    private async Task<AgentSession> RunPhaseAsync(
        AgentSession session,
        DebugRun run,
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

    private async Task PersistRunAsync(DebugRun run, CancellationToken cancellationToken)
    {
        phaseAccessor.SetActiveRun(run);
        sessionState.NotifyChanged(run);
        await runStore.SaveActiveAsync(run, cancellationToken).ConfigureAwait(false);
    }
}
