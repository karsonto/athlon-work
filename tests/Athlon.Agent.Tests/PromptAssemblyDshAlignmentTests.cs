using Athlon.Agent.Core;
using Athlon.Agent.Core.Harness;
using Athlon.Agent.Core.Prompt;
using Athlon.Agent.Core.Sso;
using Athlon.Agent.Infrastructure.Prompt;
using Athlon.Agent.Infrastructure.SubAgents;

namespace Athlon.Agent.Tests;

public sealed class PromptAssemblyDshAlignmentTests
{
    [Fact]
    public void PrepareForTurn_InterpolatesModelAndCwdVariables()
    {
        using var temp = new TempDirectoryScope("athlon-prompt-vars");
        var workspaceRoot = Path.Combine(temp.Root, "ws");
        Directory.CreateDirectory(workspaceRoot);
        var settings = new AppSettings
        {
            Model = { ModelName = "test-model-xyz" },
            Workspaces = { new WorkspaceSettings { Name = "demo", RootPath = workspaceRoot } }
        };
        var orchestrator = PromptTestHelpers.CreateOrchestrator(
            new PromptTestHelpers.FakeHostEnvironment(Path.Combine(temp.Root, "skills"), temp.Root),
            settings);
        var session = AgentSession.Create("vars") with { ActiveWorkspace = workspaceRoot };

        var prompt = orchestrator.PrepareForTurn(session, Array.Empty<ToolDefinition>()).Text;

        Assert.Contains("test-model-xyz", prompt, StringComparison.Ordinal);
        Assert.Contains(workspaceRoot, prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("{{model}}", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("{{cwd}}", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void PromptVariableInterpolator_UnknownVariable_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            PromptVariableInterpolator.Interpolate("Hello {{unknown}}", new Dictionary<string, string?>
            {
                ["model"] = "m"
            }));
        Assert.Contains("Unknown prompt variable", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FileToolsSection_OmitsGrepGuidance_WhenGrepNotAdvertised()
    {
        using var temp = new TempDirectoryScope("athlon-prompt-grep");
        var workspaceRoot = Path.Combine(temp.Root, "ws");
        Directory.CreateDirectory(workspaceRoot);
        var settings = new AppSettings
        {
            Workspaces = { new WorkspaceSettings { Name = "demo", RootPath = workspaceRoot } }
        };
        var orchestrator = PromptTestHelpers.CreateOrchestrator(
            new PromptTestHelpers.FakeHostEnvironment(Path.Combine(temp.Root, "skills"), temp.Root),
            settings);
        var session = AgentSession.Create("no-grep") with { ActiveWorkspace = workspaceRoot };
        var tools = new[]
        {
            new ToolDefinition("file_read", "read", ToolSchema.Object().Build())
        };

        var prompt = orchestrator.PrepareForTurn(session, tools).Text;

        Assert.Contains("File tools:", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("grep_files", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void AskMode_WithoutWriteTools_DoesNotEmitEditContracts()
    {
        using var temp = new TempDirectoryScope("athlon-prompt-ask");
        var workspaceRoot = Path.Combine(temp.Root, "ws");
        Directory.CreateDirectory(workspaceRoot);
        var settings = new AppSettings
        {
            Workspaces = { new WorkspaceSettings { Name = "demo", RootPath = workspaceRoot } }
        };
        var harness = RouterTestDependencies.CreateSessionHarnessState(SessionAgentMode.Ask);
        var host = new PromptTestHelpers.FakeHostEnvironment(Path.Combine(temp.Root, "skills"), temp.Root);
        IEnvironmentPromptSection[] sections =
        [
            new BasePersonaSection(),
            new AgentModeSection(),
            new HostEnvironmentSection(),
            new FileToolsPolicySection(),
            new ToolsPolicySection(),
        ];
        var orchestrator = new SystemPromptOrchestrator(
            settings,
            host,
            NullCurrentSsoUserContext.Instance,
            harness,
            sections,
            new RuntimeContextAssembler(Array.Empty<IRuntimeContextContributor>()));
        var session = AgentSession.Create("ask") with { ActiveWorkspace = workspaceRoot };
        var tools = new[]
        {
            new ToolDefinition("file_read", "read", ToolSchema.Object().Build()),
            new ToolDefinition("grep_files", "grep", ToolSchema.Object().Build()),
        };

        var prompt = orchestrator.PrepareForTurn(session, tools).Text;

        Assert.Contains("Ask mode", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("prefer apply_patch", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("file_edit old_text", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("Never retry the same old_text", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void ToolOrderCanonicalizer_AppliesListedThenRest()
    {
        var tools = new[]
        {
            new ToolDefinition("zeta", "z", ToolSchema.Object().Build()),
            new ToolDefinition("alpha", "a", ToolSchema.Object().Build()),
            new ToolDefinition("mid", "m", ToolSchema.Object().Build()),
        };
        var ordered = ToolOrderCanonicalizer.Apply(
            tools,
            ["mid", ToolOrderCanonicalizer.RestSentinel, "alpha"]);

        Assert.Equal(["mid", "zeta", "alpha"], ordered.Select(tool => tool.Name).ToArray());
    }

    [Fact]
    public void ToolOrderCanonicalizer_RejectsMissingRest()
    {
        Assert.Throws<InvalidOperationException>(() =>
            ToolOrderCanonicalizer.ValidateConfigShape(["file_read", "file_write"]));
    }

    [Fact]
    public void ToolOrderCanonicalizer_RejectsUnknownListedTool()
    {
        var tools = new[] { new ToolDefinition("alpha", "a", ToolSchema.Object().Build()) };
        Assert.Throws<InvalidOperationException>(() =>
            ToolOrderCanonicalizer.Apply(tools, ["missing", ToolOrderCanonicalizer.RestSentinel]));
    }

    [Fact]
    public void SubAgent_CompleteSystemPrompt_ReplacesEntirePrompt()
    {
        using var temp = new TempDirectoryScope("athlon-prompt-complete");
        var workspaceRoot = Path.Combine(temp.Root, "ws");
        Directory.CreateDirectory(workspaceRoot);
        var settings = new AppSettings
        {
            Model = { ModelName = "complete-model" },
            Workspaces = { new WorkspaceSettings { Name = "demo", RootPath = workspaceRoot } }
        };
        var host = new PromptTestHelpers.FakeHostEnvironment(Path.Combine(temp.Root, "skills"), temp.Root);
        var accessor = new AgentRunContextAccessor();
        var root = AgentRunContext.CreateRoot(
            AgentSession.Create("parent") with { ActiveWorkspace = workspaceRoot },
            "run",
            new ToolRouter(Array.Empty<IAgentTool>()),
            PromptTestHelpers.CreateStaticOrchestrator(),
            WorkspaceIgnoreDefaults.BuiltIn);
        var child = root.CreateChild(
            "child",
            root.ToolRouter,
            root.PromptOrchestrator,
            role: "unused role",
            loopOptions: null,
            workspaceRoot: workspaceRoot,
            ignorePatterns: WorkspaceIgnoreDefaults.BuiltIn,
            completeSystemPrompt: "ONLY COMPLETE PROMPT BODY");
        using (accessor.Push(child))
        {
            IEnvironmentPromptSection[] sections =
            [
                new SubAgentPersonaSection(accessor),
                new HostEnvironmentSection(),
                new ProductGuidanceSection(),
            ];
            var orchestrator = new SubAgentSystemPromptOrchestrator(
                settings,
                host,
                NullCurrentSsoUserContext.Instance,
                DefaultSessionHarnessState.Instance,
                sections,
                new RuntimeContextAssembler(Array.Empty<IRuntimeContextContributor>()));

            var prompt = orchestrator.PrepareForTurn(
                AgentSession.Create("child") with { ActiveWorkspace = workspaceRoot },
                Array.Empty<ToolDefinition>()).Text;

            Assert.Contains("ONLY COMPLETE PROMPT BODY", prompt, StringComparison.Ordinal);
            Assert.DoesNotContain("Host:", prompt, StringComparison.Ordinal);
            Assert.DoesNotContain("auto-compressed", prompt, StringComparison.Ordinal);
            Assert.DoesNotContain("unused role", prompt, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void RuntimeContextAssembler_SuppressWithoutComputerUse_ReturnsNull()
    {
        var accessor = new AgentRunContextAccessor();
        var session = AgentSession.Create("suppress");
        var root = AgentRunContext.CreateRoot(
            session,
            "run",
            new ToolRouter(Array.Empty<IAgentTool>()),
            PromptTestHelpers.CreateStaticOrchestrator(),
            WorkspaceIgnoreDefaults.BuiltIn) with
        {
            SuppressRuntimeContext = true,
            ComputerUseActive = false
        };
        using (accessor.Push(root))
        {
            var assembler = new RuntimeContextAssembler(
                [new HostWorkspaceRuntimeContributor()],
                accessor);
            var context = new EnvironmentPromptContext
            {
                Session = session,
                Tools = [],
                SkillsDirectory = @"C:\skills",
                Host = new PromptTestHelpers.FakeHostEnvironment(@"C:\skills", @"C:\app"),
                PromptSettings = new PromptSettings(),
            };
            Assert.Null(assembler.Build(context));
        }
    }

    [Fact]
    public void DuplicateSectionName_ThrowsAtConstruction()
    {
        Assert.Throws<InvalidOperationException>(() =>
            SystemPromptSectionAssembler.OrderAndValidate(
                [new BasePersonaSection(), new DuplicateIdentitySection()],
                PromptSectionPlacement.Static));
    }

    private sealed class DuplicateIdentitySection : IEnvironmentPromptSection
    {
        public string Name => "athlon:identity";
        public int Order => 999;
        public void Append(System.Text.StringBuilder builder, EnvironmentPromptContext context) =>
            builder.AppendLine("dup");
    }
}
