using System.Text;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Harness;
using Athlon.Agent.Core.Plan;
using Athlon.Agent.Core.Prompt;
using Athlon.Agent.Infrastructure.Prompt;

namespace Athlon.Agent.Tests.Plan;

public sealed class PlanModePromptSectionTests
{
    [Fact]
    public void Append_IncludesPublishPlanContract_InPlanMode()
    {
        var section = new PlanModePromptSection();
        var sb = new StringBuilder();
        section.Append(sb, CreateContext(SessionAgentMode.Plan));

        var text = sb.ToString();
        Assert.Contains("ask_user", text, StringComparison.Ordinal);
        Assert.Contains("publish_plan", text, StringComparison.Ordinal);
        Assert.Contains("Build", text, StringComparison.Ordinal);
        Assert.Contains("multi-turn consulting", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("auto-advances", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Append_Skips_WhenNotPlanMode()
    {
        var section = new PlanModePromptSection();
        var sb = new StringBuilder();
        section.Append(sb, CreateContext(SessionAgentMode.Coding));
        Assert.Equal(0, sb.Length);
    }

    [Fact]
    public void AgentModeSection_MentionsPlanMode()
    {
        var sb = new StringBuilder();
        new AgentModeSection().Append(sb, CreateContext(SessionAgentMode.Plan));
        var text = sb.ToString();
        Assert.Contains("Plan mode", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("publish_plan", text, StringComparison.Ordinal);
        Assert.Contains("ask_user", text, StringComparison.Ordinal);
    }

    private static EnvironmentPromptContext CreateContext(SessionAgentMode mode) =>
        new()
        {
            Session = AgentSession.Create("plan-prompt-test").WithWorkspace(@"C:\work\demo"),
            WorkspaceRoot = @"C:\work\demo",
            WorkspaceName = "demo",
            IgnorePatterns = [".git"],
            Tools =
            [
                new ToolDefinition("file_read", "Read", ToolSchema.Object().Build()),
                new ToolDefinition("publish_plan", "Publish", ToolSchema.Object().Build())
            ],
            SkillsDirectory = @"C:\Users\test\.athlon-agent\skills",
            Host = new PromptTestHelpers.FakeHostEnvironment(
                @"C:\Users\test\.athlon-agent\skills",
                @"C:\Users\test\.athlon-agent"),
            PromptSettings = new PromptSettings(),
            AgentMode = mode
        };
}

public sealed class PlanDocumentParserTests
{
    [Fact]
    public void LooksComplete_RequiresTitleStepsAcceptance()
    {
        Assert.False(Athlon.Agent.Core.Plan.PlanDocumentParser.LooksComplete("# Only title\n\nshort"));
        Assert.True(Athlon.Agent.Core.Plan.PlanDocumentParser.LooksComplete("""
            # Complete plan

            Overview paragraph that is long enough for the length gate.

            ## Steps
            1. First
            2. Second

            ## Acceptance
            - [ ] Done
            """));
    }

    [Fact]
    public void ParseTodos_FromCheckboxesAndSteps()
    {
        var todos = Athlon.Agent.Core.Plan.PlanDocumentParser.ParseTodos("""
            # Plan

            ## Steps
            1. Implement feature
            2. Add tests

            ## Acceptance
            - [ ] Feature works
            """);
        Assert.NotEmpty(todos);
    }

    [Fact]
    public void ParseTodos_PrefersNumberedSteps_OverAcceptanceCheckboxes()
    {
        var todos = Athlon.Agent.Core.Plan.PlanDocumentParser.ParseTodos("""
            # Fix auth token refresh

            Overview long enough to be a real plan document.

            ## Steps
            1. Read the token store
            2. Add a refresh timer
            3. Cover with tests

            ## Acceptance
            - [ ] Tokens refresh before expiry
            - [ ] Tests pass
            """);

        Assert.Equal(
            ["Read the token store", "Add a refresh timer", "Cover with tests"],
            todos.Select(t => t.Content));
        Assert.Equal(["todo-1", "todo-2", "todo-3"], todos.Select(t => t.Id));
        Assert.DoesNotContain(todos, t => t.Content.Contains("Tests pass", StringComparison.Ordinal));
    }

    [Fact]
    public void ParseTodos_FallsBackToCheckboxes_WhenNoStepsSection()
    {
        var todos = Athlon.Agent.Core.Plan.PlanDocumentParser.ParseTodos("""
            # Plan without a steps heading

            Do the thing carefully.

            ## Work items
            - [ ] Wire up the parser
            - [ ] Add regression tests
            """);

        Assert.Equal(["Wire up the parser", "Add regression tests"], todos.Select(t => t.Content));
    }

    [Fact]
    public void ParseTodos_NeverTreatsAcceptanceItemsAsWork()
    {
        var todos = Athlon.Agent.Core.Plan.PlanDocumentParser.ParseTodos("""
            # Plan

            Only acceptance criteria and a loose numbered list exist here.

            ## Acceptance
            - [ ] Everything works
            - [ ] No regressions

            ## Notes
            1. A numbered note that is not a step
            """);

        Assert.Equal(["A numbered note that is not a step"], todos.Select(t => t.Content));
    }

    [Fact]
    public void ParseTodos_CapsTheSeededList()
    {
        var steps = string.Join(
            Environment.NewLine,
            Enumerable.Range(1, 30).Select(i => $"{i}. Step number {i}"));
        var todos = Athlon.Agent.Core.Plan.PlanDocumentParser.ParseTodos($"# Plan\n\n## Steps\n{steps}\n");

        Assert.Equal(12, todos.Count);
    }

    [Fact]
    public void FallbackMarkdownFromAssistant_ProducesRealStepsAndNoPlaceholderTodo()
    {
        var markdown = Athlon.Agent.Core.Plan.PlanDocumentParser.FallbackMarkdownFromAssistant(
            "## Approach\n- Extract the token store\n- Add a refresh timer\n",
            "Fix token refresh");

        var todos = Athlon.Agent.Core.Plan.PlanDocumentParser.ParseTodos(markdown);

        Assert.Contains("Fix token refresh", markdown, StringComparison.Ordinal);
        Assert.True(Athlon.Agent.Core.Plan.PlanDocumentParser.LooksComplete(markdown));
        Assert.Equal(["Extract the token store", "Add a refresh timer"], todos.Select(t => t.Content));
        Assert.DoesNotContain(todos, t => t.Content.Contains("Review and refine", StringComparison.Ordinal));
    }
}

public sealed class PlanRuntimeContextContributorTests
{
    [Fact]
    public void Append_InjectsTheFullPlanText_DuringADraftRevisionTurn()
    {
        var session = AgentSession.Create("plan-revision");
        var phaseAccessor = new PlanPhaseAccessor();
        var run = new PlanRun
        {
            Id = "run-1",
            SessionId = session.Id,
            Phase = PlanPhase.Draft,
            Status = PlanRunStatuses.Draft,
            PlanMarkdown = RevisionBase
        };
        phaseAccessor.SetActiveRun(run);

        var builder = new StringBuilder();
        new PlanRuntimeContextContributor(new StubHarnessState(), phaseAccessor)
            .Append(builder, CreateContext(session));

        var text = builder.ToString();
        Assert.Contains("## Current Plan (revision base)", text, StringComparison.Ordinal);
        // The whole body must be present: once the publish_plan arguments fall out of the keep
        // window they are truncated to 20 characters, so this is the model's only readable copy.
        Assert.Contains("Read the token store before touching the refresh path", text, StringComparison.Ordinal);
        Assert.Contains("## Acceptance", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Append_OmitsTheRevisionBase_WhenTheRunHasNoMarkdown()
    {
        var session = AgentSession.Create("plan-revision");
        var phaseAccessor = new PlanPhaseAccessor();
        phaseAccessor.SetActiveRun(new PlanRun
        {
            Id = "run-1",
            SessionId = session.Id,
            Phase = PlanPhase.Draft,
            Status = PlanRunStatuses.Draft
        });

        var builder = new StringBuilder();
        new PlanRuntimeContextContributor(new StubHarnessState(), phaseAccessor)
            .Append(builder, CreateContext(session));

        Assert.DoesNotContain("## Current Plan", builder.ToString(), StringComparison.Ordinal);
    }

    private const string RevisionBase = """
        # Fix auth token refresh

        Refresh OAuth tokens before expiry.

        ## Steps
        1. Read the token store before touching the refresh path
        2. Add a refresh timer

        ## Acceptance
        - [ ] Tokens refresh before expiry
        """;

    private static EnvironmentPromptContext CreateContext(AgentSession session) =>
        new()
        {
            Session = session,
            WorkspaceRoot = @"C:\work\demo",
            WorkspaceName = "demo",
            IgnorePatterns = [".git"],
            Tools = [new ToolDefinition("publish_plan", "Publish", ToolSchema.Object().Build())],
            SkillsDirectory = @"C:\Users\test\.athlon-agent\skills",
            Host = new PromptTestHelpers.FakeHostEnvironment(
                @"C:\Users\test\.athlon-agent\skills",
                @"C:\Users\test\.athlon-agent"),
            PromptSettings = new PromptSettings(),
            AgentMode = SessionAgentMode.Plan
        };

    private sealed class StubHarnessState : ISessionHarnessState
    {
        public Task LoadAsync(string sessionId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SaveAsync(string sessionId, SessionHarnessSnapshot state, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public SessionHarnessSnapshot GetSnapshot(string? sessionId) => new(SessionAgentMode.Plan);
        public SessionAgentMode GetMode(string? sessionId) => SessionAgentMode.Plan;
        public bool IsCodingMode(string? sessionId) => false;
        public bool IsAskMode(string? sessionId) => false;
        public bool IsPlanMode(string? sessionId) => true;
        public bool IsDebugMode(string? sessionId) => false;
        public bool IsEnabled(string? sessionId) => true;
        public bool IsCodingModeForActiveRun(IAgentRunContextAccessor runContextAccessor) => false;
        public bool IsAskModeForActiveRun(IAgentRunContextAccessor runContextAccessor) => false;
        public bool IsPlanModeForActiveRun(IAgentRunContextAccessor runContextAccessor) => true;
        public bool IsDebugModeForActiveRun(IAgentRunContextAccessor runContextAccessor) => false;
        public bool IsEnabledForActiveRun(IAgentRunContextAccessor runContextAccessor) => true;
    }
}

public sealed class UserQuestionTests
{
    [Fact]
    public void FormatUserAnswer_IncludesSelectedLabelsAndNotes()
    {
        var question = new UserQuestion
        {
            RequestId = "r1",
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

        var text = UserQuestion.FormatUserAnswer(
            question,
            new Dictionary<string, IReadOnlyList<string>> { ["platform"] = ["desktop"] },
            "Use toasts");

        Assert.Contains("Which platform?: Desktop", text, StringComparison.Ordinal);
        Assert.Contains("Use toasts", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AwaitClarify_IsAwaitingUserAndReadOnly()
    {
        Assert.True(Athlon.Agent.Core.Plan.PlanPhase.AwaitClarify.IsAwaitingUser());
        Assert.True(Athlon.Agent.Core.Plan.PlanPhase.AwaitClarify.IsReadOnly());
        Assert.True(Athlon.Agent.Core.Plan.PlanPhase.AwaitClarify.BlocksMcp());
        Assert.False(Athlon.Agent.Core.Plan.PlanPhase.AwaitClarify.AllowsPublishPlan());
        Assert.True(Athlon.Agent.Core.Plan.PlanPhase.Explore.AllowsPublishPlan());
        Assert.True(Athlon.Agent.Core.Plan.PlanPhase.Draft.AllowsPublishPlan());
    }
}
