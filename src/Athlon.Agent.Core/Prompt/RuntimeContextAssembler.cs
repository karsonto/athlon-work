using System.Text;

namespace Athlon.Agent.Core.Prompt;

public sealed class RuntimeContextAssembler(IEnumerable<IRuntimeContextContributor> contributors)
{
    private readonly IReadOnlyList<IRuntimeContextContributor> _contributors =
        contributors.OrderBy(contributor => contributor.Priority).ToArray();

    public string? Build(EnvironmentPromptContext context)
    {
        var content = new StringBuilder();
        foreach (var contributor in _contributors)
        {
            contributor.Append(content, context);
        }

        var runtimeContext = content.ToString().Trim();
        if (runtimeContext.Length == 0)
        {
            return null;
        }

        var builder = new StringBuilder();
        builder.AppendLine("## Runtime context (system reminder; not a new user request)");
        builder.AppendLine();
        builder.AppendLine("<runtime_context>");
        builder.AppendLine(runtimeContext);
        builder.AppendLine("</runtime_context>");
        return builder.ToString().TrimEnd();
    }
}
