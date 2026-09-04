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
