using System.Text;

namespace Athlon.Agent.Core.Prompt;

public sealed class HostEnvironmentSection : IEnvironmentPromptSection
{
    public int Order => 200;

    public void Append(StringBuilder builder, EnvironmentPromptContext context)
    {
        var host = context.Host;
        var now = AppTimeZone.Now;

        builder.AppendLine(
            $"Host: Win {host.OsVersion} | {now:yyyy-MM-dd HH:mm} {AppTimeZone.PromptLabel} | {host.UserDomainName}\\{host.UserName} "
            + $"| cwd={host.CurrentDirectory} | skills={host.SkillsDirectory}");
        builder.AppendLine();
    }
}
