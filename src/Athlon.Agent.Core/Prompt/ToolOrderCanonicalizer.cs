using System.Text;

namespace Athlon.Agent.Core.Prompt;

/// <summary>
/// Explicit model-facing tool order with a single <see cref="RestSentinel"/> rest entry (DSH-aligned).
/// </summary>
public static class ToolOrderCanonicalizer
{
    public const string RestSentinel = "<unlisted-tools>";

    public static IReadOnlyList<ToolDefinition> Apply(
        IReadOnlyList<ToolDefinition> tools,
        IReadOnlyList<string>? toolOrder)
    {
        if (toolOrder is null || toolOrder.Count == 0)
        {
            return tools
                .OrderBy(tool => tool.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        ValidateConfigShape(toolOrder);

        var byName = new Dictionary<string, ToolDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var tool in tools)
        {
            byName[tool.Name] = tool;
        }

        var beforeRest = new List<string>();
        var afterRest = new List<string>();
        var inAfter = false;
        foreach (var entry in toolOrder)
        {
            if (string.Equals(entry, RestSentinel, StringComparison.Ordinal))
            {
                inAfter = true;
                continue;
            }

            if (inAfter)
            {
                afterRest.Add(entry);
            }
            else
            {
                beforeRest.Add(entry);
            }
        }

        var listedNames = beforeRest.Concat(afterRest).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var name in listedNames)
        {
            if (!byName.ContainsKey(name))
            {
                throw new InvalidOperationException(
                    $"Prompt ToolOrder names tool '{name}' but it is not in the visible tool set.");
            }
        }

        var ordered = new List<ToolDefinition>(tools.Count);
        foreach (var name in beforeRest)
        {
            ordered.Add(byName[name]);
        }

        foreach (var tool in tools
                     .Where(tool => !listedNames.Contains(tool.Name))
                     .OrderBy(tool => tool.Name, StringComparer.OrdinalIgnoreCase))
        {
            ordered.Add(tool);
        }

        foreach (var name in afterRest)
        {
            ordered.Add(byName[name]);
        }

        return ordered;
    }

    public static void ValidateConfigShape(IReadOnlyList<string> toolOrder)
    {
        var restCount = toolOrder.Count(entry => string.Equals(entry, RestSentinel, StringComparison.Ordinal));
        if (restCount != 1)
        {
            throw new InvalidOperationException(
                $"Prompt ToolOrder must contain exactly one '{RestSentinel}' rest entry (found {restCount}).");
        }

        var names = toolOrder
            .Where(entry => !string.Equals(entry, RestSentinel, StringComparison.Ordinal))
            .ToArray();
        if (names.Length != names.Distinct(StringComparer.OrdinalIgnoreCase).Count())
        {
            throw new InvalidOperationException("Prompt ToolOrder contains duplicate tool names.");
        }
    }
}
