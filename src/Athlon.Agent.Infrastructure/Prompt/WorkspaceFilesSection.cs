using System.Text;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Prompt;

namespace Athlon.Agent.Infrastructure.Prompt;

public sealed class WorkspaceFilesSection(ISshWorkspaceClient? sshClient = null) : IEnvironmentPromptSection
{
    public int Order => 400;

    public PromptSectionPlacement Placement => PromptSectionPlacement.PreCall;

    public void Append(StringBuilder builder, EnvironmentPromptContext context) =>
        WorkspacePromptLoader.AppendWorkspaceFiles(builder, context, sshClient);
}
