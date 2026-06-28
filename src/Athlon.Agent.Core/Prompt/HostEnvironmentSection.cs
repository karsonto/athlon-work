using System.Text;

namespace Athlon.Agent.Core.Prompt;

public sealed class HostEnvironmentSection : IEnvironmentPromptSection
{
    public int Order => 200;

    public void Append(StringBuilder builder, EnvironmentPromptContext context)
    {
        var host = context.Host;

        var hostLine =
            $"Host: Win {host.OsVersion} | tz={AppTimeZone.PromptLabel} | {host.UserDomainName}\\{host.UserName}";
        if (context.HasWorkspace)
        {
            hostLine += $" | cwd={context.WorkspaceRoot}";
        }

        hostLine += $" | skills={host.SkillsDirectory}";
        builder.AppendLine(hostLine);
        builder.AppendLine();
    }
}
