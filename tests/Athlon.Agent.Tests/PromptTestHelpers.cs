using Athlon.Agent.Core;
using Athlon.Agent.Core.Harness;
using Athlon.Agent.Core.Prompt;
using Athlon.Agent.Core.Sso;
using Athlon.Agent.Infrastructure.Prompt;
using Athlon.Agent.Skills;
using Athlon.Agent.Skills.Repository;

namespace Athlon.Agent.Tests;

internal static class PromptTestHelpers
{
    public static ISystemPromptOrchestrator CreateOrchestrator(
        IAgentHostEnvironment host,
        AppSettings? settings = null,
        IAgentSkillCatalog? catalog = null,
        IEnumerable<IRuntimeContextContributor>? runtimeContextContributors = null)
    {
        settings ??= new AppSettings();
        catalog ??= new AgentSkillCatalog(new FileSystemSkillRepository(Path.Combine(Path.GetTempPath(), "empty-skills-" + Guid.NewGuid().ToString("N"))));

        IEnvironmentPromptSection[] sections =
        [
            new BasePersonaSection(),
            new AgentModeSection(),
            new HostEnvironmentSection(),
            new EncodingPolicySection(),
            new WorkspacePolicySection(),
            new WorkspaceFilesSection(),
            new CodingWorkflowSection(),
            new FileToolsPolicySection(),
            new ToolsPolicySection(),
            new SkillsSection(settings, catalog),
            new ProductGuidanceSection()
        ];

        return new SystemPromptOrchestrator(
            settings,
            host,
            NullCurrentSsoUserContext.Instance,
            DefaultSessionHarnessState.Instance,
            sections,
            new RuntimeContextAssembler(runtimeContextContributors ?? Array.Empty<IRuntimeContextContributor>()));
    }

    public static ISystemPromptOrchestrator CreateStaticOrchestrator(string text = "prompt") =>
        new StaticSystemPromptOrchestrator(text);

    public static IAgentEnvironmentPromptBuilder CreateBuilder(
        IAgentHostEnvironment host,
        AppSettings? settings = null,
        IAgentSkillCatalog? catalog = null) =>
        new EnvironmentPromptBuilderAdapter(CreateOrchestrator(host, settings, catalog));

    public sealed class StaticSystemPromptOrchestrator(string text) : ISystemPromptOrchestrator
    {
        private readonly FrozenSystemPrompt _frozen = new(text.TrimEnd() + Environment.NewLine);

        public FrozenSystemPrompt PrepareForTurn(AgentSession session, IReadOnlyList<ToolDefinition> tools) => _frozen;

        public string? BuildRuntimeContext(AgentSession session, IReadOnlyList<ToolDefinition> tools) => null;

        public string BuildForReasoningIteration(
            FrozenSystemPrompt frozen,
            AgentSession session,
            IReadOnlyList<ToolDefinition> tools) => frozen.Text;
    }

    public sealed class FakeHostEnvironment(string skillsDirectory, string appDataDirectory) : IAgentHostEnvironment
    {
        public bool IsWindows => true;
        public string OsDescription => "Microsoft Windows 11";
        public string OsVersion => "10.0.22631.0";
        public string UserName => "karson";
        public string UserDomainName => "TESTDOMAIN";
        public string MachineName => "DESKTOP-TEST";
        public string UserProfilePath => @"C:\Users\karson";
        public string SystemDirectory => @"C:\Windows\system32";
        public string ProcessArchitecture => "X64";
        public string OsArchitecture => "X64";
        public int ProcessorCount => 8;
        public string AppDataDirectory { get; } = appDataDirectory;
        public string SkillsDirectory { get; } = skillsDirectory;
    }
}
