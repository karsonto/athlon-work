using System.Text;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Harness;
using Athlon.Agent.Core.Prompt;
using Athlon.Agent.Core.Sso;
using Athlon.Agent.Infrastructure.Prompt;

namespace Athlon.Agent.Infrastructure.SubAgents;

public sealed class SubAgentSystemPromptOrchestrator(
    AppSettings settings,
    IAgentHostEnvironment host,
    ICurrentSsoUserContext ssoUserContext,
    ISessionHarnessState harnessState,
    IEnumerable<IEnvironmentPromptSection> sections,
    RuntimeContextAssembler runtimeContextAssembler) : ISystemPromptOrchestrator
{
    private static readonly HashSet<Type> ExcludedSectionTypes =
    [
        typeof(BasePersonaSection),
        typeof(ProductGuidanceSection),
        typeof(SubAgentDelegationSection),
        typeof(WorkspaceFilesSection),
    ];

    private readonly IReadOnlyList<IEnvironmentPromptSection> _staticSections =
        FilterSections(sections, PromptSectionPlacement.Static);

    private readonly IReadOnlyList<IEnvironmentPromptSection> _preCallSections =
        FilterSections(sections, PromptSectionPlacement.PreCall);

    public FrozenSystemPrompt PrepareForTurn(AgentSession session, IReadOnlyList<ToolDefinition> tools)
    {
        var context = CreateContext(session, tools);
        var builder = new StringBuilder();
        var occupancy = EnvironmentPromptOccupancy.AppendSections(builder, context, _staticSections)
            .Combine(EnvironmentPromptOccupancy.AppendSections(builder, context, _preCallSections));
        return new FrozenSystemPrompt(FormatPrompt(builder), occupancy);
    }

    public string BuildForReasoningIteration(
        FrozenSystemPrompt frozen,
        AgentSession session,
        IReadOnlyList<ToolDefinition> tools) =>
        frozen.Text;

    public string? BuildRuntimeContext(AgentSession session, IReadOnlyList<ToolDefinition> tools) =>
        runtimeContextAssembler.Build(CreateContext(session, tools));

    private static IReadOnlyList<IEnvironmentPromptSection> FilterSections(
        IEnumerable<IEnvironmentPromptSection> sections,
        PromptSectionPlacement placement) =>
        sections.Where(section => section.Placement == placement && !ExcludedSectionTypes.Contains(section.GetType()))
            .OrderBy(section => section.Order)
            .ToArray();

    private EnvironmentPromptContext CreateContext(AgentSession session, IReadOnlyList<ToolDefinition> tools)
    {
        var workspace = ResolveWorkspace(session);
        return new EnvironmentPromptContext
        {
            Session = session,
            WorkspaceRoot = workspace?.RootPath,
            WorkspaceName = workspace?.Name,
            WorkspaceKind = workspace?.WorkspaceKind ?? WorkspaceKind.Local,
            IgnorePatterns = WorkspaceIgnoreResolver.Resolve(
                workspacePatterns: workspace?.IgnorePatterns,
                globalPatterns: settings.WorkspaceIgnore.DirectoryNames),
            Tools = tools,
            SkillsDirectory = host.SkillsDirectory,
            Host = host,
            PromptSettings = settings.Prompt,
            SsoUserDisplayName = ssoUserContext.DisplayName,
            AgentMode = harnessState.GetMode(session.Id),
        };
    }

    private static string FormatPrompt(StringBuilder builder) =>
        builder.ToString().TrimEnd() + Environment.NewLine;

    private WorkspaceSettings? ResolveWorkspace(AgentSession session)
    {
        var match = WorkspaceSessionResolver.FindMatch(session, settings);
        if (!string.IsNullOrWhiteSpace(session.ActiveWorkspace))
        {
            var kind = match?.WorkspaceKind
                ?? (string.IsNullOrWhiteSpace(session.ActiveWorkspaceId) ? WorkspaceKind.Local : WorkspaceKind.Ssh);
            var rootPath = kind == WorkspaceKind.Ssh
                ? RemotePathNormalizer.NormalizeRoot(session.ActiveWorkspace)
                : Path.GetFullPath(session.ActiveWorkspace);
            return new WorkspaceSettings
            {
                Id = match?.Id ?? session.ActiveWorkspaceId ?? string.Empty,
                Name = match?.Name
                    ?? (kind == WorkspaceKind.Ssh
                        ? RemotePathNormalizer.GetFileName(rootPath)
                        : Path.GetFileName(rootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))),
                Kind = WorkspaceKinds.ToSettingsValue(kind),
                RootPath = rootPath,
                IgnorePatterns = WorkspaceIgnoreResolver.Resolve(
                    workspacePatterns: match?.IgnorePatterns,
                    globalPatterns: settings.WorkspaceIgnore.DirectoryNames).ToList(),
                Ssh = match?.Ssh
            };
        }

        return null;
    }
}
