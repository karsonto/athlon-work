using System.Text;

namespace Athlon.Agent.Core.Prompt;

public interface IRuntimeContextContributor
{
    int Priority { get; }

    void Append(StringBuilder builder, EnvironmentPromptContext context);
}
