using System.Text;
using Athlon.Agent.Core.Harness;
using Athlon.Agent.Core.Sso;

namespace Athlon.Agent.Core.Prompt;

public sealed class SystemPromptOrchestrator(
    AppSettings settings,
    IAgentHostEnvironment host,
    ICurrentSsoUserContext ssoUserContext,
    ISessionHarnessState harnessState,
    IEnumerable<IEnvironmentPromptSection> sections,
    RuntimeContextAssembler runtimeContextAssembler) : ISystemPromptOrchestrator
{
    private readonly IReadOnlyList<IEnvironmentPromptSection> _staticSections =
        sections.Where(section => section.Placement == PromptSectionPlacement.Static)
            .OrderBy(section => section.Order)
            .ToArray();

    private readonly IReadOnlyList<IEnvironmentPromptSection> _preCallSections =
        sections.Where(section => section.Placement == PromptSectionPlacement.PreCall)
            .OrderBy(section => section.Order)
            .ToArray();

    public FrozenSystemPrompt PrepareForTurn(AgentSession session, IReadOnlyList<ToolDefinition> tools)
    {
        var context = CreateContext(session, tools);
        var builder = new StringBuilder();

        AppendSections(builder, context, _staticSections);
        AppendSections(builder, context, _preCallSections);

        return new FrozenSystemPrompt(FormatPrompt(builder));
    }

    public string BuildForReasoningIteration(
        FrozenSystemPrompt frozen,
        AgentSession session,
        IReadOnlyList<ToolDefinition> tools) =>
        frozen.Text;

    public string? BuildRuntimeContext(AgentSession session, IReadOnlyList<ToolDefinition> tools) =>
        runtimeContextAssembler.Build(CreateContext(session, tools));

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

    private static void AppendSections(
        StringBuilder builder,
        EnvironmentPromptContext context,
        IReadOnlyList<IEnvironmentPromptSection> sections)
    {
        foreach (var section in sections)
        {
            section.Append(builder, context);
        }
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
