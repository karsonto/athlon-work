using System.Text;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Plan;
using Athlon.Agent.Core.Prompt;
using Athlon.Agent.Infrastructure.Prompt;
using Athlon.Agent.Skills;
using Athlon.Agent.Skills.Repository;

namespace Athlon.Agent.Tests;

public sealed class SystemPromptOrchestratorTests
{
    [Fact]
    public void PrepareForTurn_MatchesLegacyBuilderOutput()
    {
        var host = new PromptTestHelpers.FakeHostEnvironment(
            @"C:\Users\test\.athlon-agent\skills",
            @"C:\Users\test\.athlon-agent");
        var settings = new AppSettings
        {
            Workspaces = { new WorkspaceSettings { Name = "demo", RootPath = @"C:\work\demo" } }
        };
        var catalog = new AgentSkillCatalog(new FileSystemSkillRepository(Path.Combine(Path.GetTempPath(), "parity-skills-" + Guid.NewGuid().ToString("N"))));
        IEnvironmentPromptSection[] sections =
        [
            new BasePersonaSection(),
            new HostEnvironmentSection(),
            new WorkspacePolicySection(),
            new WorkspaceFilesSection(),
            new FileToolsPolicySection(),
            new ToolsPolicySection(),
            new SkillsSection(settings, catalog),
            new ProductGuidanceSection()
        ];

        var orchestrator = new SystemPromptOrchestrator(settings, host, sections, Array.Empty<IPreReasoningPromptContributor>());
        var legacy = new AgentEnvironmentPromptBuilder(settings, host, sections);

        var session = AgentSession.Create("orchestrator-parity");
        var tools = Array.Empty<ToolDefinition>();

        var frozen = orchestrator.PrepareForTurn(session, tools);
        var legacyText = legacy.Build(session, tools);

        Assert.Equal(legacyText, frozen.Text);
    }

    [Fact]
    public void BuildForReasoningIteration_WithoutContributors_ReturnsFrozenText()
    {
        var orchestrator = PromptTestHelpers.CreateOrchestrator(
            new PromptTestHelpers.FakeHostEnvironment(
                @"C:\Users\test\.athlon-agent\skills",
                @"C:\Users\test\.athlon-agent"));

        var session = AgentSession.Create("frozen-parity");
        var tools = Array.Empty<ToolDefinition>();
        var frozen = orchestrator.PrepareForTurn(session, tools);
        var iteration = orchestrator.BuildForReasoningIteration(frozen, session, tools);

        Assert.Equal(frozen.Text, iteration);
    }

    [Fact]
    public void BuildForReasoningIteration_WithContributor_AppendsContent()
    {
        var orchestrator = PromptTestHelpers.CreateOrchestrator(
            new PromptTestHelpers.FakeHostEnvironment(
                @"C:\Users\test\.athlon-agent\skills",
                @"C:\Users\test\.athlon-agent"),
            preReasoningContributors: [new TestPreReasoningContributor()]);

        var session = AgentSession.Create("pre-reasoning");
        var tools = Array.Empty<ToolDefinition>();
        var frozen = orchestrator.PrepareForTurn(session, tools);
        var iteration = orchestrator.BuildForReasoningIteration(frozen, session, tools);

        Assert.StartsWith(frozen.Text.TrimEnd(), iteration.TrimEnd(), StringComparison.Ordinal);
        Assert.Contains("PRE_REASONING_MARKER", iteration, StringComparison.Ordinal);
    }

    [Fact]
    public void PrepareForTurn_IncludesGranularPlanningAndAutoContinueGuidance()
    {
        var host = new PromptTestHelpers.FakeHostEnvironment(
            @"C:\Users\test\.athlon-agent\skills",
            @"C:\Users\test\.athlon-agent");
        var settings = new AppSettings
        {
            Workspaces = { new WorkspaceSettings { Name = "demo", RootPath = @"C:\work\demo" } },
            Plan = new PlanSettings { AutoContinueEnabled = true, MaxSubtasks = 12 }
        };
        var orchestrator = PromptTestHelpers.CreateOrchestrator(host, settings);
        var prompt = orchestrator.PrepareForTurn(AgentSession.Create("plan-guidance"), Array.Empty<ToolDefinition>()).Text;

        Assert.Contains("granular subtasks", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("up to 12", prompt, StringComparison.Ordinal);
        Assert.Contains("auto-send a continue instruction", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("long tasks require create_plan with granular subtasks", prompt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EnvironmentPromptBuilderAdapter_MatchesPrepareForTurn()
    {
        var host = new PromptTestHelpers.FakeHostEnvironment(
            @"C:\Users\test\.athlon-agent\skills",
            @"C:\Users\test\.athlon-agent");
        var orchestrator = PromptTestHelpers.CreateOrchestrator(host);
        var adapter = new EnvironmentPromptBuilderAdapter(orchestrator);
        var session = AgentSession.Create("adapter");

        Assert.Equal(
            orchestrator.PrepareForTurn(session, Array.Empty<ToolDefinition>()).Text,
            adapter.Build(session, Array.Empty<ToolDefinition>()));
    }

    private sealed class TestPreReasoningContributor : IPreReasoningPromptContributor
    {
        public int Priority => 100;

        public void Append(StringBuilder builder, EnvironmentPromptContext context) =>
            builder.AppendLine("PRE_REASONING_MARKER");
    }
}
