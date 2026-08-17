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
    public async Task ContinueAsync_VerifiedFixedFromAwaitReproGoesToDone()
    {
        var session = AgentSession.Create("debug-session");
        var store = new InMemoryDebugRunStore();
        var phaseAccessor = new DebugPhaseAccessor();
        var sessionState = new DebugSessionState();
        var orchestrator = new StubAgentOrchestrator(_ =>
        [
            "Removed athlon-debug probes."
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
        session = await sut.ContinueAsync(session, DebugContinuationKind.VerifiedFixed, null, CancellationToken.None);

        run = phaseAccessor.GetActiveRun(session.Id);
        Assert.NotNull(run);
        Assert.Equal(DebugPhase.Done, run.Phase);
        Assert.Equal(1, orchestrator.TurnCount);
    }

    [Fact]
    public async Task ContinueAsync_ReproducedAdvancesToAwaitVerify()
    {
        var session = AgentSession.Create("debug-session");
        var store = new InMemoryDebugRunStore();
        var phaseAccessor = new DebugPhaseAccessor();
        var sessionState = new DebugSessionState();
        var orchestrator = new StubAgentOrchestrator(_ =>
        [
            "Root cause: loop uses `<` instead of `<=`.",
            "Fixed loop bound."
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
        Assert.Equal(DebugPhase.AwaitVerify, run.Phase);
    }

    private sealed class StubAgentOrchestrator(Func<int, IReadOnlyList<string>> responses) : IAgentOrchestrator
    {
        private int _turn;

        public int TurnCount => _turn;

        public Task<AgentSession> SendAsync(
            AgentSession session,
            string userInput,
            IReadOnlyList<ImageAttachment>? imageAttachments = null,
            AgentTurnCallbacks? callbacks = null,
            CancellationToken cancellationToken = default,
            bool computerUseActive = false)
        {
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
