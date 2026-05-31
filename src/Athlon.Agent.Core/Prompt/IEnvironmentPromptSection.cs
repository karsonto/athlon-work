using System.Text;

namespace Athlon.Agent.Core.Prompt;

public interface IEnvironmentPromptSection
{
    int Order { get; }

    PromptSectionPlacement Placement => PromptSectionPlacement.Static;

    void Append(StringBuilder builder, EnvironmentPromptContext context);
}
