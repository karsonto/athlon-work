using System.Text;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Harness;
using Athlon.Agent.Core.Prompt;
using Athlon.Agent.Core.Sso;
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
            new EncodingPolicySection(),
            new WorkspacePolicySection(),
            new WorkspaceFilesSection(),
            new CodingWorkflowSection(),
            new ToolsPolicySection(),
            new SkillsSection(settings, catalog),
            new ProductGuidanceSection()
        ];

        var orchestrator = new SystemPromptOrchestrator(
            settings,
            host,
            NullCurrentSsoUserContext.Instance,
            DefaultSessionHarnessState.Instance,
            sections,
            new RuntimeContextAssembler(Array.Empty<IRuntimeContextContributor>()));
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
    public void BuildRuntimeContext_WithContributor_ReturnsEphemeralContent()
    {
        var orchestrator = PromptTestHelpers.CreateOrchestrator(
            new PromptTestHelpers.FakeHostEnvironment(
                @"C:\Users\test\.athlon-agent\skills",
                @"C:\Users\test\.athlon-agent"),
            runtimeContextContributors: [new TestRuntimeContextContributor()]);

        var session = AgentSession.Create("pre-reasoning");
        var tools = Array.Empty<ToolDefinition>();
        var frozen = orchestrator.PrepareForTurn(session, tools);
        var iteration = orchestrator.BuildForReasoningIteration(frozen, session, tools);
        var runtimeContext = orchestrator.BuildRuntimeContext(session, tools);

        Assert.Equal(frozen.Text, iteration);
        Assert.NotNull(runtimeContext);
        Assert.Contains("<runtime_context>", runtimeContext, StringComparison.Ordinal);
        Assert.Contains("PRE_REASONING_MARKER", runtimeContext, StringComparison.Ordinal);
    }

    [Fact]
    public void PrepareForTurn_IncludesCodingWorkflow_WhenWorkspaceConfigured()
    {
        var settings = new AppSettings
        {
            Workspaces = { new WorkspaceSettings { Name = "demo", RootPath = @"C:\work\demo" } }
        };
        var orchestrator = PromptTestHelpers.CreateOrchestrator(
            new PromptTestHelpers.FakeHostEnvironment(@"C:\Users\test\.athlon-agent\skills", @"C:\Users\test\.athlon-agent"),
            settings);
        var session = AgentSession.Create("coding-workflow") with { ActiveWorkspace = @"C:\work\demo" };
        var prompt = orchestrator.PrepareForTurn(session, Array.Empty<ToolDefinition>()).Text;

        Assert.Contains("Coding workflow:", prompt, StringComparison.Ordinal);
        Assert.Contains("mvn -q -pl", prompt, StringComparison.Ordinal);
        Assert.Contains("state a brief plan before editing", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void PrepareForTurn_ExcludesLegacyPlanGuidance_InAgentMode()
    {
        var host = new PromptTestHelpers.FakeHostEnvironment(
            @"C:\Users\test\.athlon-agent\skills",
            @"C:\Users\test\.athlon-agent");
        var settings = new AppSettings
        {
            Workspaces = { new WorkspaceSettings { Name = "demo", RootPath = @"C:\work\demo" } }
        };
        var orchestrator = PromptTestHelpers.CreateOrchestrator(host, settings);
        var prompt = orchestrator.PrepareForTurn(AgentSession.Create("agent-mode"), Array.Empty<ToolDefinition>()).Text;

        Assert.DoesNotContain("Plan mode (spec-first workflow)", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("finish_subtask", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("Session Plan mode workflow:", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("create_plan", prompt, StringComparison.Ordinal);
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

    private sealed class TestRuntimeContextContributor : IRuntimeContextContributor
    {
        public int Priority => 100;

        public void Append(StringBuilder builder, EnvironmentPromptContext context) =>
            builder.AppendLine("PRE_REASONING_MARKER");
    }
}
