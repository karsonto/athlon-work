using System.Text;

namespace Athlon.Agent.Core.Prompt;

public sealed class HostEnvironmentSection : IEnvironmentPromptSection
{
    public int Order => 200;

    public void Append(StringBuilder builder, EnvironmentPromptContext context)
    {
        var host = context.Host;
        builder.AppendLine(
            $"Host: Win {host.OsVersion} | tz={AppTimeZone.PromptLabel} | skills=available");
        builder.AppendLine();
    }
}
