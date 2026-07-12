using System.Text;
using System.Security.Cryptography;

namespace Athlon.Agent.Core.Prompt;

public sealed class RuntimeContextAssembler(IEnumerable<IRuntimeContextContributor> contributors)
{
    private readonly IReadOnlyList<IRuntimeContextContributor> _contributors =
        contributors.OrderBy(contributor => contributor.Priority).ToArray();

    public string? Build(EnvironmentPromptContext context) => BuildSnapshot(context)?.Content;

    public RuntimeContextSnapshot? BuildSnapshot(EnvironmentPromptContext context)
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
        var wrapped = builder.ToString().TrimEnd();
        return new RuntimeContextSnapshot(wrapped, RuntimeContextSnapshot.ComputeFingerprint(wrapped));
    }
}

public sealed record RuntimeContextSnapshot(string Content, string Fingerprint)
{
    public static string ComputeFingerprint(string? content)
    {
        var bytes = Encoding.UTF8.GetBytes(content ?? string.Empty);
        return Convert.ToHexStringLower(SHA256.HashData(bytes))[..16];
    }
}
