using Athlon.Agent.Core;
using Athlon.Agent.Core.Debug;

namespace Athlon.Agent.Tests.Debug;

public sealed class DebugTurnOrchestratorTests
{
    [Fact]
    public async Task RunUserTurnAsync_ReachesAwaitRepro()
    {
        var session = AgentSession.Create("debug-session");
        var store = new InMemoryDebugRunStore();
        var phaseAccessor = new DebugPhaseAccessor();
        var sessionState = new DebugSessionState();
        var orchestrator = new StubAgentOrchestrator(responses =>
        [
            """
            H1: Off-by-one in loop bound
            H2: Stale cache entry
            """,
            """
            Added probes.

            ## Repro steps
            1. Run the app
            2. Trigger save
            """
        ]);

        var sut = new DebugTurnOrchestrator(orchestrator, store, phaseAccessor, sessionState);
        session = await sut.RunUserTurnAsync(session, "Save button drops the last item", null, CancellationToken.None);

        var run = phaseAccessor.GetActiveRun(session.Id);
        Assert.NotNull(run);
        Assert.Equal(DebugPhase.AwaitRepro, run.Phase);
        Assert.Equal(2, run.Hypotheses.Count);
        Assert.Contains("Trigger save", run.ReproStepsMarkdown, StringComparison.Ordinal);
        Assert.True(sut.IsAwaitingUser(session.Id));
        Assert.Equal(new List<bool> { true, false }, orchestrator.AppendUserMessageFlags);
    }

    [Fact]
    public async Task RunUserTurnAsync_FollowUpReusesActiveRunWithoutAdvancingPhase()
    {
        var session = AgentSession.Create("debug-session");
        var store = new InMemoryDebugRunStore();
        var phaseAccessor = new DebugPhaseAccessor();
        var sessionState = new DebugSessionState();
        var orchestrator = new StubAgentOrchestrator(responses =>
        [
            """
            H1: Off-by-one in loop bound
            H2: Stale cache entry
            """,
            """
            Added probes.

            ## Repro steps
            1. Run the app
            2. Trigger save
            """,
            "Thanks, I reproduced it on Windows."
        ]);

        var sut = new DebugTurnOrchestrator(orchestrator, store, phaseAccessor, sessionState);
        session = await sut.RunUserTurnAsync(session, "Save button drops the last item", null, CancellationToken.None);
        var first = phaseAccessor.GetActiveRun(session.Id);
        Assert.NotNull(first);

        session = await sut.RunUserTurnAsync(session, "Reproduced on Windows 11.", null, CancellationToken.None);
        var followUp = phaseAccessor.GetActiveRun(session.Id);
        Assert.NotNull(followUp);
        Assert.Equal(first.Id, followUp.Id);
        Assert.Equal(DebugPhase.AwaitRepro, followUp.Phase);
        Assert.Equal(3, orchestrator.TurnCount);
    }

    [Fact]
    public async Task RunUserTurnAsync_FallsBackToH1_WhenHypothesesUnparsed()
    {
        var session = AgentSession.Create("debug-session");
        var store = new InMemoryDebugRunStore();
        var phaseAccessor = new DebugPhaseAccessor();
        var sessionState = new DebugSessionState();
        var orchestrator = new StubAgentOrchestrator(_ =>
        [
            "I think the cache is stale but I will not use the required format.",
            "Still no H-lines here.",
            """
            Added probes.

            ## Repro steps
            1. Retry save
            """
        ]);

        var sut = new DebugTurnOrchestrator(orchestrator, store, phaseAccessor, sessionState);
        session = await sut.RunUserTurnAsync(session, "Save drops the last item", null, CancellationToken.None);

        var run = phaseAccessor.GetActiveRun(session.Id);
        Assert.NotNull(run);
        Assert.Equal(DebugPhase.AwaitRepro, run.Phase);
        Assert.Single(run.Hypotheses);
        Assert.Equal("H1", run.Hypotheses[0].Id);
        Assert.Contains("Still no H-lines", run.Hypotheses[0].Summary, StringComparison.Ordinal);
        Assert.Equal(3, orchestrator.TurnCount);
        Assert.Equal(new List<bool> { true, false, false }, orchestrator.AppendUserMessageFlags);
    }

    [Fact]
    public async Task ContinueAsync_VerifiedFixedFromAwaitReproFails()
    {
        var session = AgentSession.Create("debug-session");
        var store = new InMemoryDebugRunStore();
        var phaseAccessor = new DebugPhaseAccessor();
        var sessionState = new DebugSessionState();
        var orchestrator = new StubAgentOrchestrator(_ => ["should not run"]);

        var run = new DebugRun
        {
            Id = "run1",
            SessionId = session.Id,
            LogPath = store.CreateLogPath("run1"),
            Phase = DebugPhase.AwaitRepro,
            BugDescription = "bug",
            ReproStepsMarkdown = "repro"
        };
        await store.SaveActiveAsync(run);
        phaseAccessor.SetActiveRun(run);

        var sut = new DebugTurnOrchestrator(orchestrator, store, phaseAccessor, sessionState);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sut.ContinueAsync(session, DebugContinuationKind.VerifiedFixed, null, CancellationToken.None));
        Assert.Equal(0, orchestrator.TurnCount);
    }

    [Fact]
    public async Task ContinueAsync_ReproducedStopsAtAwaitFixConfirm()
    {
        var session = AgentSession.Create("debug-session");
        var store = new InMemoryDebugRunStore();
        var phaseAccessor = new DebugPhaseAccessor();
        var sessionState = new DebugSessionState();
        var orchestrator = new StubAgentOrchestrator(_ =>
        [
            "Root cause: loop uses `<` instead of `<=`."
        ]);

        var run = new DebugRun
        {
            Id = "run1",
            SessionId = session.Id,
            LogPath = store.CreateLogPath("run1"),
            Phase = DebugPhase.AwaitRepro,
            BugDescription = "bug",
            ReproStepsMarkdown = "repro"
        };
        await store.SaveActiveAsync(run);
        phaseAccessor.SetActiveRun(run);

        var sut = new DebugTurnOrchestrator(orchestrator, store, phaseAccessor, sessionState);
        session = await sut.ContinueAsync(session, DebugContinuationKind.Reproduced, null, CancellationToken.None);

        run = phaseAccessor.GetActiveRun(session.Id);
        Assert.NotNull(run);
        Assert.Equal(DebugPhase.AwaitFixConfirm, run.Phase);
        Assert.Contains("Root cause", run.RootCauseSummary, StringComparison.Ordinal);
        Assert.Equal(new List<bool> { false }, orchestrator.AppendUserMessageFlags);
    }

    [Fact]
    public async Task ContinueAsync_StartFixAdvancesToAwaitVerify()
    {
        var session = AgentSession.Create("debug-session");
        var store = new InMemoryDebugRunStore();
        var phaseAccessor = new DebugPhaseAccessor();
        var sessionState = new DebugSessionState();
        var orchestrator = new StubAgentOrchestrator(_ =>
        [
            "Fixed loop bound."
        ]);

        var run = new DebugRun
        {
            Id = "run1",
            SessionId = session.Id,
            LogPath = store.CreateLogPath("run1"),
            Phase = DebugPhase.AwaitFixConfirm,
            BugDescription = "bug",
            RootCauseSummary = "off-by-one"
        };
        await store.SaveActiveAsync(run);
        phaseAccessor.SetActiveRun(run);

        var sut = new DebugTurnOrchestrator(orchestrator, store, phaseAccessor, sessionState);
        session = await sut.ContinueAsync(session, DebugContinuationKind.StartFix, null, CancellationToken.None);

        run = phaseAccessor.GetActiveRun(session.Id);
        Assert.NotNull(run);
        Assert.Equal(DebugPhase.AwaitVerify, run.Phase);
        Assert.Equal(new List<bool> { false }, orchestrator.AppendUserMessageFlags);
    }

    [Fact]
    public async Task ContinueAsync_ReanalyzeStaysAtAwaitFixConfirm()
    {
        var session = AgentSession.Create("debug-session");
        var store = new InMemoryDebugRunStore();
        var phaseAccessor = new DebugPhaseAccessor();
        var sessionState = new DebugSessionState();
        var orchestrator = new StubAgentOrchestrator(_ =>
        [
            "Re-read logs: evidence still points to the loop bound."
        ]);

        var run = new DebugRun
        {
            Id = "run1",
            SessionId = session.Id,
            LogPath = store.CreateLogPath("run1"),
            Phase = DebugPhase.AwaitFixConfirm,
            BugDescription = "bug",
            RootCauseSummary = "old summary"
        };
        await store.SaveActiveAsync(run);
        phaseAccessor.SetActiveRun(run);

        var sut = new DebugTurnOrchestrator(orchestrator, store, phaseAccessor, sessionState);
        session = await sut.ContinueAsync(session, DebugContinuationKind.Reanalyze, null, CancellationToken.None);

        run = phaseAccessor.GetActiveRun(session.Id);
        Assert.NotNull(run);
        Assert.Equal(DebugPhase.AwaitFixConfirm, run.Phase);
        Assert.Contains("loop bound", run.RootCauseSummary, StringComparison.Ordinal);
        Assert.Equal(new List<bool> { false }, orchestrator.AppendUserMessageFlags);
    }

    private sealed class StubAgentOrchestrator(Func<int, IReadOnlyList<string>> responses) : IAgentOrchestrator
    {
        private int _turn;

        public int TurnCount => _turn;

        public List<bool> AppendUserMessageFlags { get; } = [];

        public Task<AgentSession> SendAsync(
            AgentSession session,
            string userInput,
            IReadOnlyList<ImageAttachment>? imageAttachments = null,
            AgentTurnCallbacks? callbacks = null,
            CancellationToken cancellationToken = default,
            bool computerUseActive = false,
            bool appendUserMessage = true)
        {
            AppendUserMessageFlags.Add(appendUserMessage);
            var list = responses(_turn++);
            var content = list[Math.Min(_turn - 1, list.Count - 1)];
            session = session.WithMessage(ChatMessage.Create(MessageRole.Assistant, content));
            return Task.FromResult(session);
        }
    }

    private sealed class InMemoryDebugRunStore : IDebugRunStore
    {
        private readonly Dictionary<string, DebugRun> _runs = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _active = new(StringComparer.OrdinalIgnoreCase);

        public string CreateLogPath(string runId) => Path.Combine(Path.GetTempPath(), "athlon-debug-" + runId + ".jsonl");

        public Task<DebugRun?> LoadActiveAsync(string sessionId, CancellationToken cancellationToken = default)
        {
            if (!_active.TryGetValue(sessionId, out var runId))
            {
                return Task.FromResult<DebugRun?>(null);
            }

            _runs.TryGetValue(runId, out var run);
            return Task.FromResult(run);
        }

        public Task SaveActiveAsync(DebugRun run, CancellationToken cancellationToken = default)
        {
            _runs[run.Id] = run.Clone();
            _active[run.SessionId] = run.Id;
            return Task.CompletedTask;
        }

        public Task SaveRunAsync(DebugRun run, CancellationToken cancellationToken = default)
        {
            _runs[run.Id] = run.Clone();
            return Task.CompletedTask;
        }

        public Task<DebugRun?> LoadRunAsync(string sessionId, string runId, CancellationToken cancellationToken = default)
        {
            _runs.TryGetValue(runId, out var run);
            return Task.FromResult(run);
        }

        public Task ClearActiveAsync(string sessionId, CancellationToken cancellationToken = default)
        {
            _active.Remove(sessionId);
            return Task.CompletedTask;
        }
    }
}
