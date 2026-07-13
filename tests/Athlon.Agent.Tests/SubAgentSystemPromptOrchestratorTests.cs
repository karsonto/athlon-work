using Athlon.Agent.Core;
using Athlon.Agent.Core.Harness;
using Athlon.Agent.Core.Prompt;
using Athlon.Agent.Core.Sso;
using Athlon.Agent.Infrastructure.Prompt;
using Athlon.Agent.Infrastructure.SubAgents;

namespace Athlon.Agent.Tests;

public sealed class SubAgentSystemPromptOrchestratorTests
{
    [Fact]
    public void PrepareForTurn_ExcludesWorkspaceFilesIncludingAgentsMd()
    {
        using var temp = new TempDirectoryScope("athlon-subagent-prompt");
        var workspaceRoot = Path.Combine(temp.Root, "workspace");
        Directory.CreateDirectory(workspaceRoot);
        File.WriteAllText(Path.Combine(workspaceRoot, "AGENTS.md"), "# Agent Rules\nAlways run tests.");
        File.WriteAllText(Path.Combine(workspaceRoot, "CONTRIBUTING.md"), "# Contributing\nUse conventional commits.");

        var settings = new AppSettings
        {
            Workspaces =
            [
                new WorkspaceSettings { RootPath = workspaceRoot }
            ]
        };
        var host = new PromptTestHelpers.FakeHostEnvironment(
            Path.Combine(temp.Root, "skills"),
            Path.Combine(temp.Root, "app-data"));
        IEnvironmentPromptSection[] sections =
        [
            new BasePersonaSection(),
            new WorkspaceFilesSection(),
            new ProductGuidanceSection(),
            new SubAgentDelegationSection(settings),
        ];

        var subAgentOrchestrator = new SubAgentSystemPromptOrchestrator(
            settings,
            host,
            NullCurrentSsoUserContext.Instance,
            DefaultSessionHarnessState.Instance,
            sections,
            new RuntimeContextAssembler(Array.Empty<IRuntimeContextContributor>()));
        var mainOrchestrator = new SystemPromptOrchestrator(
            settings,
            host,
            NullCurrentSsoUserContext.Instance,
            DefaultSessionHarnessState.Instance,
            sections,
            new RuntimeContextAssembler(Array.Empty<IRuntimeContextContributor>()));

        var session = AgentSession.Create("sub-agent") with { ActiveWorkspace = workspaceRoot };
        var tools = Array.Empty<ToolDefinition>();

        var subAgentPrompt = subAgentOrchestrator.PrepareForTurn(session, tools).Text;
        var mainPrompt = mainOrchestrator.PrepareForTurn(session, tools).Text;

        Assert.DoesNotContain("## AGENTS.md", subAgentPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain("## CONTRIBUTING.md", subAgentPrompt, StringComparison.Ordinal);
        Assert.Contains("## AGENTS.md", mainPrompt, StringComparison.Ordinal);
        Assert.Contains("## CONTRIBUTING.md", mainPrompt, StringComparison.Ordinal);
    }
}
