using System.Text;

namespace Athlon.Agent.Core.Prompt;

public sealed class HostEnvironmentSection : IEnvironmentPromptSection
{
    public string Name => "host:env";

    public int Order => PromptSectionBands.Host;

    public void Append(StringBuilder builder, EnvironmentPromptContext context)
    {
        var host = context.Host;
        builder.AppendLine(
            $"Host: Win {host.OsVersion} | tz={AppTimeZone.PromptLabel} | model={{{{model}}}} | skills=available");
        builder.AppendLine();
    }
}
