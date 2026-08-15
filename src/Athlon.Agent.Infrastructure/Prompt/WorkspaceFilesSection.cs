using System.Text;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Prompt;

namespace Athlon.Agent.Infrastructure.Prompt;

public sealed class WorkspaceFilesSection(ISshWorkspaceClient? sshClient = null) : IEnvironmentPromptSection
{
    public string Name => "workspace:files";

    public int Order => PromptSectionBands.WorkflowStart + 1;

    public PromptSectionPlacement Placement => PromptSectionPlacement.PreCall;

    public PromptOccupancyKind OccupancyKind => PromptOccupancyKind.Rules;

    public void Append(StringBuilder builder, EnvironmentPromptContext context) =>
        WorkspacePromptLoader.AppendWorkspaceFiles(builder, context, sshClient);
}
