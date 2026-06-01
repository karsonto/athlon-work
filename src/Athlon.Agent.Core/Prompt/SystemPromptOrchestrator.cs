using System.Text;

namespace Athlon.Agent.Core.Prompt;

public sealed class SystemPromptOrchestrator(
    AppSettings settings,
    IAgentHostEnvironment host,
    IEnumerable<IEnvironmentPromptSection> sections,
    IEnumerable<IPreReasoningPromptContributor> preReasoningContributors) : ISystemPromptOrchestrator
{
    private readonly IReadOnlyList<IEnvironmentPromptSection> _staticSections =
        sections.Where(section => section.Placement == PromptSectionPlacement.Static)
            .OrderBy(section => section.Order)
            .ToArray();

    private readonly IReadOnlyList<IEnvironmentPromptSection> _preCallSections =
        sections.Where(section => section.Placement == PromptSectionPlacement.PreCall)
            .OrderBy(section => section.Order)
            .ToArray();

    private readonly IReadOnlyList<IPreReasoningPromptContributor> _preReasoningContributors =
        preReasoningContributors.OrderBy(contributor => contributor.Priority).ToArray();

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
        IReadOnlyList<ToolDefinition> tools)
    {
        if (_preReasoningContributors.Count == 0)
        {
            return frozen.Text;
        }

        var context = CreateContext(session, tools);
        var builder = new StringBuilder(frozen.Text);

        foreach (var contributor in _preReasoningContributors)
        {
            contributor.Append(builder, context);
        }

        return FormatPrompt(builder);
    }

    private EnvironmentPromptContext CreateContext(AgentSession session, IReadOnlyList<ToolDefinition> tools)
    {
        var workspace = ResolveWorkspace(session);
        return new EnvironmentPromptContext
        {
            Session = session,
            WorkspaceRoot = workspace?.RootPath,
            WorkspaceName = workspace?.Name,
            IgnorePatterns = WorkspaceIgnoreResolver.Resolve(
                workspacePatterns: workspace?.IgnorePatterns,
                globalPatterns: settings.WorkspaceIgnore.DirectoryNames),
            Tools = tools,
            SkillsDirectory = host.SkillsDirectory,
            Host = host,
            PromptSettings = settings.Prompt,
            PlanAutoContinueEnabled = settings.Plan.AutoContinueEnabled,
            PlanMaxSubtasks = settings.Plan.MaxSubtasks
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
        if (!string.IsNullOrWhiteSpace(session.ActiveWorkspace))
        {
            var rootPath = Path.GetFullPath(session.ActiveWorkspace);
            var match = settings.Workspaces.FirstOrDefault(workspace =>
                !string.IsNullOrWhiteSpace(workspace.RootPath)
                && string.Equals(Path.GetFullPath(workspace.RootPath), rootPath, StringComparison.OrdinalIgnoreCase));

            return new WorkspaceSettings
            {
                Name = Path.GetFileName(rootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
                RootPath = rootPath,
                IgnorePatterns = WorkspaceIgnoreResolver.Resolve(
                    workspacePatterns: match?.IgnorePatterns,
                    globalPatterns: settings.WorkspaceIgnore.DirectoryNames).ToList()
            };
        }

        return settings.Workspaces.FirstOrDefault(workspace => !string.IsNullOrWhiteSpace(workspace.RootPath));
    }
}
