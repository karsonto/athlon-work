using Athlon.Agent.Core;
using Athlon.Agent.Core.Plan;

namespace Athlon.Agent.Tests.Plan;

public sealed class PlanTurnOrchestratorTests
{
    [Fact]
    public async Task RunUserTurnAsync_ExploreThenDraftThenAwaitConfirm()
    {
        var session = AgentSession.Create("plan-session");
        var store = new InMemoryPlanRunStore();
        var phaseAccessor = new PlanPhaseAccessor();
        var sessionState = new PlanSessionState();
        var completePlan = """
            # Fix auth token refresh

            Refresh OAuth tokens before expiry.

            ## Steps
            1. Read token store
            2. Add refresh timer
            3. Cover with tests

            ## Acceptance
            - [ ] Tokens refresh before expiry
            - [ ] Tests pass
            """;
        var orchestrator = new StubAgentOrchestrator(_ =>
        [
            "Explored the auth module; token refresh is missing.",
            "Calling publish_plan with the plan."
        ]);

        var sut = new PlanTurnOrchestrator(orchestrator, store, phaseAccessor, sessionState);
        // Seed publish_plan content after explore, before draft seal by writing during draft turn.
        orchestrator.OnTurn = turn =>
        {
            if (turn == 1)
            {
                store.WritePlanMarkdownAsync(session.Id, completePlan).GetAwaiter().GetResult();
            }
        };

        session = await sut.RunUserTurnAsync(session, "Add token refresh", null, CancellationToken.None);

        var run = phaseAccessor.GetActiveRun(session.Id);
        Assert.NotNull(run);
        Assert.Equal(PlanPhase.AwaitConfirm, run.Phase);
        Assert.Equal(PlanRunStatuses.AwaitingConfirmation, PlanRunStatuses.Normalize(run.Status));
        Assert.Contains("token", run.PlanMarkdown, StringComparison.OrdinalIgnoreCase);
        Assert.True(sut.IsAwaitingUser(session.Id));
        Assert.Equal(new List<bool> { true, false }, orchestrator.AppendUserMessageFlags);
    }

    [Fact]
    public async Task RunUserTurnAsync_FollowUpInAwaitConfirm_DoesNotAdvancePhase()
    {
        var session = AgentSession.Create("plan-session");
        var store = new InMemoryPlanRunStore();
        var phaseAccessor = new PlanPhaseAccessor();
        var sessionState = new PlanSessionState();
        var completePlan = """
            # Feature X

            Overview of X.

            ## Steps
            1. Do A

            ## Acceptance
            - [ ] Done
            """;
        await store.WritePlanMarkdownAsync(session.Id, completePlan);
        var run = new PlanRun
        {
            Id = "run1",
            SessionId = session.Id,
            Phase = PlanPhase.AwaitConfirm,
            Status = PlanRunStatuses.AwaitingConfirmation,
            Goal = "Feature X",
            PlanMarkdown = completePlan,
            PlanPath = store.GetPlanMarkdownPath(session.Id)
        };
        await store.SaveActiveAsync(run);
        phaseAccessor.SetActiveRun(run);

        var orchestrator = new StubAgentOrchestrator(_ => ["Clarifying: we will use option B."]);
        var sut = new PlanTurnOrchestrator(orchestrator, store, phaseAccessor, sessionState);
        session = await sut.RunUserTurnAsync(session, "Prefer option B", null, CancellationToken.None);

        var followUp = phaseAccessor.GetActiveRun(session.Id);
        Assert.NotNull(followUp);
        Assert.Equal(PlanPhase.AwaitConfirm, followUp.Phase);
        Assert.Equal(run.Id, followUp.Id);
        Assert.Equal(1, orchestrator.TurnCount);
    }

    [Fact]
    public async Task ContinueAsync_Build_ApprovesAndCompletes()
    {
        var session = AgentSession.Create("plan-session");
        var store = new InMemoryPlanRunStore();
        var phaseAccessor = new PlanPhaseAccessor();
        var sessionState = new PlanSessionState();
        var markdown = """
            # Ship feature

            Overview.

            ## Steps
            1. Implement

            ## Acceptance
            - [ ] Works
            """;
        await store.WritePlanMarkdownAsync(session.Id, markdown);
        var run = new PlanRun
        {
            Id = "run1",
            SessionId = session.Id,
            Phase = PlanPhase.AwaitConfirm,
            Status = PlanRunStatuses.AwaitingConfirmation,
            PlanMarkdown = markdown,
            PlanPath = store.GetPlanMarkdownPath(session.Id),
            Todos = [new PlanTodoItem { Id = "impl", Content = "Implement feature" }]
        };
        await store.SaveActiveAsync(run);
        phaseAccessor.SetActiveRun(run);

        var orchestrator = new StubAgentOrchestrator(_ => []);
        var sut = new PlanTurnOrchestrator(orchestrator, store, phaseAccessor, sessionState);
        await sut.ContinueAsync(session, PlanContinuationKind.Build, null, CancellationToken.None);

        var done = phaseAccessor.GetActiveRun(session.Id);
        Assert.NotNull(done);
        Assert.Equal(PlanPhase.Done, done.Phase);
        Assert.Equal(PlanRunStatuses.Approved, PlanRunStatuses.Normalize(done.Status));
        Assert.Equal(0, orchestrator.TurnCount);
        Assert.False(sut.IsAwaitingUser(session.Id));
    }

    [Fact]
    public async Task ContinueAsync_Revise_ReturnsToDraftThenAwait()
    {
        var session = AgentSession.Create("plan-session");
        var store = new InMemoryPlanRunStore();
        var phaseAccessor = new PlanPhaseAccessor();
        var sessionState = new PlanSessionState();
        var revised = """
            # Revised plan

            New overview.

            ## Steps
            1. New step

            ## Acceptance
            - [ ] Revised OK
            """;
        var run = new PlanRun
        {
            Id = "run1",
            SessionId = session.Id,
            Phase = PlanPhase.AwaitConfirm,
            Status = PlanRunStatuses.AwaitingConfirmation,
            Goal = "goal",
            PlanPath = store.GetPlanMarkdownPath(session.Id)
        };
        await store.SaveActiveAsync(run);
        phaseAccessor.SetActiveRun(run);

        var orchestrator = new StubAgentOrchestrator(_ => ["Republishing plan."]);
        orchestrator.OnTurn = _ =>
        {
            store.WritePlanMarkdownAsync(session.Id, revised).GetAwaiter().GetResult();
        };

        var sut = new PlanTurnOrchestrator(orchestrator, store, phaseAccessor, sessionState);
        session = await sut.ContinueAsync(
            session,
            PlanContinuationKind.Revise,
            null,
            CancellationToken.None,
            userInput: "Please revise the overview");

        var after = phaseAccessor.GetActiveRun(session.Id);
        Assert.NotNull(after);
        Assert.Equal(PlanPhase.AwaitConfirm, after.Phase);
        Assert.Contains("Revised", after.PlanMarkdown, StringComparison.Ordinal);
        Assert.Contains(true, orchestrator.AppendUserMessageFlags);
    }

    private sealed class StubAgentOrchestrator(Func<int, IReadOnlyList<string>> responses) : IAgentOrchestrator
    {
        private int _turn;

        public int TurnCount => _turn;

        public List<bool> AppendUserMessageFlags { get; } = [];

        public Action<int>? OnTurn { get; set; }

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
            OnTurn?.Invoke(_turn);
            var list = responses(_turn++);
            var content = list.Count == 0
                ? ""
                : list[Math.Min(_turn - 1, list.Count - 1)];
            if (!string.IsNullOrEmpty(content))
            {
                session = session.WithMessage(ChatMessage.Create(MessageRole.Assistant, content));
            }

            return Task.FromResult(session);
        }
    }

    private sealed class InMemoryPlanRunStore : IPlanRunStore
    {
        private readonly Dictionary<string, PlanRun> _runs = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _active = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _markdown = new(StringComparer.OrdinalIgnoreCase);
        private readonly string _root = Path.Combine(Path.GetTempPath(), "athlon-plan-tests-" + Guid.NewGuid().ToString("N"));

        public string GetPlanMarkdownPath(string sessionId) =>
            Path.Combine(_root, sessionId, "plan.md");

        public Task WritePlanMarkdownAsync(string sessionId, string markdown, CancellationToken cancellationToken = default)
        {
            _markdown[sessionId] = markdown ?? string.Empty;
            Directory.CreateDirectory(Path.GetDirectoryName(GetPlanMarkdownPath(sessionId))!);
            File.WriteAllText(GetPlanMarkdownPath(sessionId), markdown ?? string.Empty);
            return Task.CompletedTask;
        }

        public Task<string?> ReadPlanMarkdownAsync(string sessionId, CancellationToken cancellationToken = default) =>
            Task.FromResult(_markdown.TryGetValue(sessionId, out var md) ? md : null);

        public Task<PlanRun?> LoadActiveAsync(string sessionId, CancellationToken cancellationToken = default)
        {
            if (!_active.TryGetValue(sessionId, out var runId))
            {
                return Task.FromResult<PlanRun?>(null);
            }

            _runs.TryGetValue(runId, out var run);
            return Task.FromResult(run?.Clone());
        }

        public Task SaveActiveAsync(PlanRun run, CancellationToken cancellationToken = default)
        {
            _runs[run.Id] = run.Clone();
            _active[run.SessionId] = run.Id;
            return Task.CompletedTask;
        }

        public Task SaveRunAsync(PlanRun run, CancellationToken cancellationToken = default)
        {
            _runs[run.Id] = run.Clone();
            return Task.CompletedTask;
        }

        public Task<PlanRun?> LoadRunAsync(string sessionId, string runId, CancellationToken cancellationToken = default)
        {
            _runs.TryGetValue(runId, out var run);
            return Task.FromResult(run?.Clone());
        }

        public Task ClearActiveAsync(string sessionId, CancellationToken cancellationToken = default)
        {
            _active.Remove(sessionId);
            return Task.CompletedTask;
        }

        public Task<PlanRun?> LoadApprovedAsync(string sessionId, CancellationToken cancellationToken = default)
        {
            PlanRun? best = null;
            foreach (var run in _runs.Values)
            {
                if (!string.Equals(run.SessionId, sessionId, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!string.Equals(PlanRunStatuses.Normalize(run.Status), PlanRunStatuses.Approved, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (best is null || run.UpdatedAt > best.UpdatedAt)
                {
                    best = run;
                }
            }

            return Task.FromResult(best?.Clone());
        }
    }
}
