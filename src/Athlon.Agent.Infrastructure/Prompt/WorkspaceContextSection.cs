using System.Text;
using Athlon.Agent.Core.Prompt;

namespace Athlon.Agent.Infrastructure.Prompt;

/// <summary>Stable workspace identity in the frozen prefix (path/name only, no file contents).</summary>
public sealed class WorkspaceContextSection : IEnvironmentPromptSection
{
    public int Order => 350;

    public PromptSectionPlacement Placement => PromptSectionPlacement.Static;

    public void Append(StringBuilder builder, EnvironmentPromptContext context)
    {
        if (!context.HasWorkspace || string.IsNullOrWhiteSpace(context.WorkspaceRoot))
        {
            return;
        }

        builder.AppendLine();
        builder.AppendLine("## Workspace");
        builder.AppendLine($"Root: {context.WorkspaceRoot}");
        if (!string.IsNullOrWhiteSpace(context.WorkspaceName))
        {
            builder.AppendLine($"Name: {context.WorkspaceName}");
        }

        builder.AppendLine();
    }
}
