using System.Text;
using System.Text.Json;
using Athlon.Agent.Core.Prompt;

namespace Athlon.Agent.Infrastructure.Prompt;

public sealed class WorkspaceFilesPromptContributor : IPreReasoningPromptContributor
{
    public int Priority => 150;

    public void Append(StringBuilder builder, EnvironmentPromptContext context) =>
        WorkspacePromptLoader.AppendWorkspaceFiles(builder, context);
}
