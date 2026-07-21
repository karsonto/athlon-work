using System.Text;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Prompt;

namespace Athlon.Agent.Infrastructure.Prompt;

/// <summary>
/// Injects SSO display name, host user, skills path, and workspace identity into runtime context
/// so the frozen system prefix stays cache-stable across users and workspaces.
/// </summary>
public sealed class HostWorkspaceRuntimeContributor : IRuntimeContextContributor
{
    public int Priority => 10;

    public void Append(StringBuilder builder, EnvironmentPromptContext context)
    {
        if (!string.IsNullOrWhiteSpace(context.SsoUserDisplayName))
        {
            builder.AppendLine(
                $"The signed-in user is {context.SsoUserDisplayName}. Address them by name when appropriate.");
        }

        var host = context.Host;
        builder.AppendLine($"Host user: {host.UserDomainName}\\{host.UserName}");
        builder.AppendLine($"Skills directory: {host.SkillsDirectory}");

        if (!context.HasWorkspace || string.IsNullOrWhiteSpace(context.WorkspaceRoot))
        {
            return;
        }

        var kind = context.WorkspaceKind == WorkspaceKind.Ssh ? "ssh" : "local";
        builder.AppendLine($"Workspace kind: {kind}");
        if (!string.IsNullOrWhiteSpace(context.WorkspaceName))
        {
            builder.AppendLine($"Workspace name: {context.WorkspaceName}");
        }

        builder.AppendLine($"Workspace root: {context.WorkspaceRoot}");
    }
}
