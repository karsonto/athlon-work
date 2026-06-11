using System.Text;
using Athlon.Agent.Core.Prompt;

namespace Athlon.Agent.Infrastructure.Prompt;

public sealed class WorkspaceFilesSection : IEnvironmentPromptSection
{
    public int Order => 400;

    public PromptSectionPlacement Placement => PromptSectionPlacement.PreCall;

    public void Append(StringBuilder builder, EnvironmentPromptContext context)
    {
        // Workspace file contents are injected per reasoning iteration via WorkspaceFilesPromptContributor.
    }
}
