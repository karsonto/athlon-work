using Athlon.Agent.Core;
using Athlon.Agent.Core.Plan;
using Athlon.Agent.Infrastructure.Plan;

namespace Athlon.Agent.Tests.Plan;

public sealed class PlanTurnOrchestratorTests
{
    private const string CompleteAuthPlan = """
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

    [Fact]
    public async Task RunUserTurnAsync_ExplorePublishes_SealsAwaitConfirm()
    {
        var session = AgentSession.Create("plan-session");
        var store = new InMemoryPlanRunStore();
        var phaseAccessor = new PlanPhaseAccessor();
        var sessionState = new PlanSessionState();
        var userQuestions = new UserQuestionState();
        var orchestrator = new StubAgentOrchestrator(_ => ["Explored and publishing."]);
        orchestrator.OnTurn = _ =>
        {
            store.WritePlanMarkdownAsync(session.Id, CompleteAuthPlan).GetAwaiter().GetResult();
        };

        var sut = new PlanTurnOrchestrator(orchestrator, store, phaseAccessor, sessionState, userQuestions);
        session = await sut.RunUserTurnAsync(session, "Add token refresh", null, CancellationToken.None);

        var run = phaseAccessor.GetActiveRun(session.Id);
        Assert.NotNull(run);
        Assert.Equal(PlanPhase.AwaitConfirm, run.Phase);
        Assert.Equal(PlanRunStatuses.AwaitingConfirmation, PlanRunStatuses.Normalize(run.Status));
        Assert.Contains("token", run.PlanMarkdown, StringComparison.OrdinalIgnoreCase);
        Assert.True(sut.IsAwaitingUser(session.Id));
        Assert.Equal(1, orchestrator.TurnCount);
        Assert.Equal(new List<bool> { true }, orchestrator.AppendUserMessageFlags);
    }

    [Fact]
    public async Task RunUserTurnAsync_ExploreWithoutPlan_StaysExplore()
    {
        var session = AgentSession.Create("plan-session");
        var store = new InMemoryPlanRunStore();
        var phaseAccessor = new PlanPhaseAccessor();
        var sessionState = new PlanSessionState();
        var userQuestions = new UserQuestionState();
        var orchestrator = new StubAgentOrchestrator(_ => ["Still gathering context."]);

        var sut = new PlanTurnOrchestrator(orchestrator, store, phaseAccessor, sessionState, userQuestions);
        session = await sut.RunUserTurnAsync(session, "Add notifications", null, CancellationToken.None);

        var run = phaseAccessor.GetActiveRun(session.Id);
        Assert.NotNull(run);
        Assert.Equal(PlanPhase.Explore, run.Phase);
        Assert.False(sut.IsAwaitingUser(session.Id));
        Assert.Equal(1, orchestrator.TurnCount);
    }

    [Fact]
    public async Task RunUserTurnAsync_FollowUpInExplore_PublishesAwaitConfirm()
    {
        var session = AgentSession.Create("plan-session");
        var store = new InMemoryPlanRunStore();
        var phaseAccessor = new PlanPhaseAccessor();
        var sessionState = new PlanSessionState();
        var userQuestions = new UserQuestionState();
        var run = new PlanRun
        {
            Id = "run1",
            SessionId = session.Id,
            Phase = PlanPhase.Explore,
            Status = PlanRunStatuses.Draft,
            Goal = "Add token refresh"
        };
        await store.SaveActiveAsync(run);
        phaseAccessor.SetActiveRun(run);

        var orchestrator = new StubAgentOrchestrator(_ => ["Ready to publish."]);
        orchestrator.OnTurn = _ =>
        {
            store.WritePlanMarkdownAsync(session.Id, CompleteAuthPlan).GetAwaiter().GetResult();
        };

        var sut = new PlanTurnOrchestrator(orchestrator, store, phaseAccessor, sessionState, userQuestions);
        session = await sut.RunUserTurnAsync(session, "Use sliding refresh", null, CancellationToken.None);

        var after = phaseAccessor.GetActiveRun(session.Id);
        Assert.NotNull(after);
        Assert.Equal(PlanPhase.AwaitConfirm, after.Phase);
        Assert.Contains("token", after.PlanMarkdown, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, orchestrator.TurnCount);
    }

    [Fact]
    public async Task RunUserTurnAsync_FollowUpInAwaitConfirm_RevisesPlan()
    {
        var session = AgentSession.Create("plan-session");
        var store = new InMemoryPlanRunStore();
        var phaseAccessor = new PlanPhaseAccessor();
        var sessionState = new PlanSessionState();
        var userQuestions = new UserQuestionState();
        var completePlan = """
            # Feature X

            Overview of X.

            ## Steps
            1. Do A

            ## Acceptance
            - [ ] Done
            """;
        var revised = """
            # Feature X

            Prefer option B.

            ## Steps
            1. Do B

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
            PlanMarkdown = completePlan
        };
        await store.SaveActiveAsync(run);
        phaseAccessor.SetActiveRun(run);

        var orchestrator = new StubAgentOrchestrator(_ => ["Republishing with option B."]);
        orchestrator.OnTurn = _ =>
        {
            store.WritePlanMarkdownAsync(session.Id, revised).GetAwaiter().GetResult();
        };
        var sut = new PlanTurnOrchestrator(orchestrator, store, phaseAccessor, sessionState, userQuestions);
        session = await sut.RunUserTurnAsync(session, "Prefer option B", null, CancellationToken.None);

        var followUp = phaseAccessor.GetActiveRun(session.Id);
        Assert.NotNull(followUp);
        Assert.Equal(PlanPhase.AwaitConfirm, followUp.Phase);
        Assert.Contains("option B", followUp.PlanMarkdown, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(run.Id, followUp.Id);
        Assert.Equal(1, orchestrator.TurnCount);
    }

    [Fact]
    public async Task RunUserTurnAsync_ExploreAsksClarification_StopsBeforePublish()
    {
        var session = AgentSession.Create("plan-session");
        var store = new InMemoryPlanRunStore();
        var phaseAccessor = new PlanPhaseAccessor();
        var sessionState = new PlanSessionState();
        var userQuestions = new UserQuestionState();
        var orchestrator = new StubAgentOrchestrator(_ => ["Need to know the target platform."]);
        orchestrator.OnTurn = turn =>
        {
            if (turn != 0)
            {
                return;
            }

            // Mirrors AskUserTool.InvokeAsync: park the run in AwaitClarify and hold
            // the question set in the in-memory UserQuestionState.
            userQuestions.SetPending(
                session.Id,
                new UserQuestion
                {
                    RequestId = "q1",
                    Questions =
                    [
                        new UserQuestionItem
                        {
                            Id = "platform",
                            Prompt = "Which platform?",
                            Options =
                            [
                                new UserQuestionOption { Id = "web", Label = "Web" },
                                new UserQuestionOption { Id = "desktop", Label = "Desktop" }
                            ]
                        }
                    ]
                });
            var current = phaseAccessor.GetActiveRun(session.Id);
            Assert.NotNull(current);
            current.Phase = PlanPhase.AwaitClarify;
            current.Status = PlanRunStatuses.AwaitingClarification;
            phaseAccessor.SetActiveRun(current);
            store.SaveActiveAsync(current).GetAwaiter().GetResult();
        };

        var sut = new PlanTurnOrchestrator(orchestrator, store, phaseAccessor, sessionState, userQuestions);
        session = await sut.RunUserTurnAsync(session, "Add notifications", null, CancellationToken.None);

        var run = phaseAccessor.GetActiveRun(session.Id);
        Assert.NotNull(run);
        Assert.Equal(PlanPhase.AwaitClarify, run.Phase);
        Assert.Equal(PlanRunStatuses.AwaitingClarification, PlanRunStatuses.Normalize(run.Status));
        Assert.NotNull(userQuestions.GetPending(session.Id));
        Assert.True(sut.IsAwaitingUser(session.Id));
        Assert.Equal(1, orchestrator.TurnCount);
    }

    [Fact]
    public async Task RunUserTurnAsync_AnswerClarification_ThenPublishesPlan()
    {
        var session = AgentSession.Create("plan-session");
        var store = new InMemoryPlanRunStore();
        var phaseAccessor = new PlanPhaseAccessor();
        var sessionState = new PlanSessionState();
        var userQuestions = new UserQuestionState();
        var completePlan = """
            # Desktop notifications

            Use Windows toasts.

            ## Steps
            1. Add toast helper

            ## Acceptance
            - [ ] Toasts appear
            """;
        var question = new UserQuestion
        {
            RequestId = "q1",
            Questions =
            [
                new UserQuestionItem
                {
                    Id = "platform",
                    Prompt = "Which platform?",
                    Options =
                    [
                        new UserQuestionOption { Id = "web", Label = "Web" },
                        new UserQuestionOption { Id = "desktop", Label = "Desktop" }
                    ]
                }
            ]
        };
        userQuestions.SetPending(session.Id, question);
        var run = new PlanRun
        {
            Id = "run1",
            SessionId = session.Id,
            Phase = PlanPhase.AwaitClarify,
            Status = PlanRunStatuses.AwaitingClarification,
            Goal = "Add notifications"
        };
        await store.SaveActiveAsync(run);
        phaseAccessor.SetActiveRun(run);

        var orchestrator = new StubAgentOrchestrator(_ => ["Publishing desktop plan."]);
        orchestrator.OnTurn = _ =>
        {
            store.WritePlanMarkdownAsync(session.Id, completePlan).GetAwaiter().GetResult();
        };

        var sut = new PlanTurnOrchestrator(orchestrator, store, phaseAccessor, sessionState, userQuestions);
        session = await sut.RunUserTurnAsync(
            session,
            UserQuestion.FormatUserAnswer(
                question,
                new Dictionary<string, IReadOnlyList<string>> { ["platform"] = ["desktop"] },
                null),
            null,
            CancellationToken.None);

        var after = phaseAccessor.GetActiveRun(session.Id);
        Assert.NotNull(after);
        Assert.Equal(PlanPhase.AwaitConfirm, after.Phase);
        Assert.Null(userQuestions.GetPending(session.Id));
        Assert.Contains("toast", after.PlanMarkdown, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, orchestrator.TurnCount);
        Assert.Equal(new List<bool> { true }, orchestrator.AppendUserMessageFlags);
    }

    [Fact]
    public async Task RunUserTurnAsync_AnswerClarification_WithoutPlan_StaysExplore()
    {
        var session = AgentSession.Create("plan-session");
        var store = new InMemoryPlanRunStore();
        var phaseAccessor = new PlanPhaseAccessor();
        var sessionState = new PlanSessionState();
        var userQuestions = new UserQuestionState();
        var question = new UserQuestion
        {
            RequestId = "q1",
            Questions =
            [
                new UserQuestionItem
                {
                    Id = "platform",
                    Prompt = "Which platform?",
                    Options =
                    [
                        new UserQuestionOption { Id = "web", Label = "Web" },
                        new UserQuestionOption { Id = "desktop", Label = "Desktop" }
                    ]
                }
            ]
        };
        userQuestions.SetPending(session.Id, question);
        var run = new PlanRun
        {
            Id = "run1",
            SessionId = session.Id,
            Phase = PlanPhase.AwaitClarify,
            Status = PlanRunStatuses.AwaitingClarification,
            Goal = "Add notifications"
        };
        await store.SaveActiveAsync(run);
        phaseAccessor.SetActiveRun(run);

        var orchestrator = new StubAgentOrchestrator(_ => ["Thanks; I'll dig into toast APIs next."]);
        var sut = new PlanTurnOrchestrator(orchestrator, store, phaseAccessor, sessionState, userQuestions);
        await sut.RunUserTurnAsync(
            session,
            UserQuestion.FormatUserAnswer(
                question,
                new Dictionary<string, IReadOnlyList<string>> { ["platform"] = ["desktop"] },
                null),
            null,
            CancellationToken.None);

        var after = phaseAccessor.GetActiveRun(session.Id);
        Assert.NotNull(after);
        Assert.Equal(PlanPhase.Explore, after.Phase);
        Assert.Null(userQuestions.GetPending(session.Id));
        Assert.False(sut.IsAwaitingUser(session.Id));
        Assert.Equal(1, orchestrator.TurnCount);
    }

    [Fact]
    public async Task ContinueAsync_Build_MarksApprovedFromInMemoryMarkdown()
    {
        var session = AgentSession.Create("plan-session");
        var store = new InMemoryPlanRunStore();
        var phaseAccessor = new PlanPhaseAccessor();
        var sessionState = new PlanSessionState();
        var userQuestions = new UserQuestionState();
        var stale = """
            # Ship feature

            Stale overview.

            ## Steps
            1. Implement

            ## Acceptance
            - [ ] Works
            """;
        var published = """
            # Ship feature

            Published latest.

            ## Steps
            1. Implement edited step

            ## Acceptance
            - [ ] Edited works
            """;
        await store.WritePlanMarkdownAsync(session.Id, published);
        var run = new PlanRun
        {
            Id = "run1",
            SessionId = session.Id,
            Phase = PlanPhase.AwaitConfirm,
            Status = PlanRunStatuses.AwaitingConfirmation,
            PlanMarkdown = stale,
            Todos = [new PlanTodoItem { Id = "impl", Content = "Implement feature" }]
        };
        await store.SaveActiveAsync(run);
        phaseAccessor.SetActiveRun(run);

        var orchestrator = new StubAgentOrchestrator(_ => []);
        var sut = new PlanTurnOrchestrator(orchestrator, store, phaseAccessor, sessionState, userQuestions);
        await sut.ContinueAsync(session, PlanContinuationKind.Build, null, CancellationToken.None);

        var done = phaseAccessor.GetActiveRun(session.Id);
        Assert.NotNull(done);
        Assert.Equal(PlanPhase.Done, done.Phase);
        Assert.Equal(PlanRunStatuses.Approved, PlanRunStatuses.Normalize(done.Status));
        Assert.Contains("Published latest", done.PlanMarkdown, StringComparison.Ordinal);
        Assert.Contains("Edited works", done.Todos.Select(t => t.Content));
    }

    [Fact]
    public async Task ContinueAsync_Revise_ReturnsToDraftThenAwait()
    {
        var session = AgentSession.Create("plan-session");
        var store = new InMemoryPlanRunStore();
        var phaseAccessor = new PlanPhaseAccessor();
        var sessionState = new PlanSessionState();
        var userQuestions = new UserQuestionState();
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
            Goal = "goal"
        };
        await store.SaveActiveAsync(run);
        phaseAccessor.SetActiveRun(run);

        var orchestrator = new StubAgentOrchestrator(_ => ["Republishing plan."]);
        orchestrator.OnTurn = _ =>
        {
            store.WritePlanMarkdownAsync(session.Id, revised).GetAwaiter().GetResult();
        };

        var sut = new PlanTurnOrchestrator(orchestrator, store, phaseAccessor, sessionState, userQuestions);
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
}
