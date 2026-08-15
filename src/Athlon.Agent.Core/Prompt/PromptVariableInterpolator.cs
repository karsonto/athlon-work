using System.Text;
using System.Text.RegularExpressions;

namespace Athlon.Agent.Core.Prompt;

/// <summary>
/// Strict <c>{{name}}</c> interpolation aligned with DSH: unknown or empty registered values fail loud;
/// a lone <c>{{</c> with no closing <c>}}</c> remains verbatim; substituted values are not rescanned.
/// </summary>
public static partial class PromptVariableInterpolator
{
    [GeneratedRegex(@"\{\{([^{}]+)\}\}", RegexOptions.CultureInvariant)]
    private static partial Regex VariableGroupRegex();

    public static string Interpolate(string text, IReadOnlyDictionary<string, string?> variables)
    {
        if (string.IsNullOrEmpty(text) || !text.Contains("{{", StringComparison.Ordinal))
        {
            return text;
        }

        return VariableGroupRegex().Replace(text, match =>
        {
            var name = match.Groups[1].Value;
            if (name.Length == 0 || name.Contains('{', StringComparison.Ordinal) || name.Contains('}', StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Malformed prompt variable group: {match.Value}");
            }

            if (!variables.TryGetValue(name, out var value))
            {
                throw new InvalidOperationException($"Unknown prompt variable '{{{{{name}}}}}'.");
            }

            if (string.IsNullOrEmpty(value))
            {
                throw new InvalidOperationException(
                    $"Prompt variable '{{{{{name}}}}}' is registered but has no value for this assembly.");
            }

            return value;
        });
    }
}
