using System.Text;

namespace Athlon.Agent.Core.Prompt;

public sealed class SignedInUserSection : IEnvironmentPromptSection
{
    public int Order => 95;

    public void Append(StringBuilder builder, EnvironmentPromptContext context)
    {
        if (string.IsNullOrWhiteSpace(context.SsoUserDisplayName))
        {
            return;
        }

        builder.AppendLine($"The signed-in user is {context.SsoUserDisplayName}. Address them by name when appropriate.");
        builder.AppendLine();
    }
}
