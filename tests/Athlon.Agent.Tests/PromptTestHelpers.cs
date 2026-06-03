using Athlon.Agent.Core;
using Athlon.Agent.Core.Plan;
using Athlon.Agent.Core.Prompt;
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
        IPlanNotebook? planNotebook = null,
        IEnumerable<IPreReasoningPromptContributor>? preReasoningContributors = null)
    {
        settings ??= new AppSettings();
        planNotebook ??= new NoOpPlanNotebook();
        catalog ??= new AgentSkillCatalog(new FileSystemSkillRepository(Path.Combine(Path.GetTempPath(), "empty-skills-" + Guid.NewGuid().ToString("N"))));

        IEnvironmentPromptSection[] sections =
        [
            new BasePersonaSection(),
            new HostEnvironmentSection(),
            new WorkspacePolicySection(),
            new PlanModePolicySection(),
            new PlanExecutionPolicySection(),
            new WorkspaceFilesSection(),
            new FileToolsPolicySection(),
            new ToolsPolicySection(),
            new SkillsSection(settings, catalog),
            new ProductGuidanceSection()
        ];

        return new SystemPromptOrchestrator(
            settings,
            host,
            planNotebook,
            sections,
            preReasoningContributors ?? Array.Empty<IPreReasoningPromptContributor>());
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
        public string CurrentDirectory => @"C:\Users\karson\athlon-work";
        public string SystemDirectory => @"C:\Windows\system32";
        public string ProcessArchitecture => "X64";
        public string OsArchitecture => "X64";
        public int ProcessorCount => 8;
        public string AppDataDirectory { get; } = appDataDirectory;
        public string SkillsDirectory { get; } = skillsDirectory;
    }
}
