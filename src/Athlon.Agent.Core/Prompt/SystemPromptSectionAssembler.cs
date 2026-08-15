using System.Text;
using Athlon.Agent.Core.Compaction;

namespace Athlon.Agent.Core.Prompt;

/// <summary>
/// Shared section assembly: duplicate-name check, empty-section drop, complete-section override,
/// variable interpolation, and occupancy accounting.
/// </summary>
public static class SystemPromptSectionAssembler
{
    public static IReadOnlyList<IEnvironmentPromptSection> OrderAndValidate(
        IEnumerable<IEnvironmentPromptSection> sections,
        PromptSectionPlacement placement)
    {
        var ordered = sections
            .Where(section => section.Placement == placement)
            .OrderBy(section => section.Order)
            .ToArray();

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var section in ordered)
        {
            if (string.IsNullOrWhiteSpace(section.Name))
            {
                throw new InvalidOperationException(
                    $"Prompt section of type {section.GetType().Name} has an empty Name.");
            }

            if (!seen.Add(section.Name))
            {
                throw new InvalidOperationException(
                    $"Duplicate prompt section name '{section.Name}' in placement {placement}.");
            }
        }

        return ordered;
    }

    public static (string Text, PromptOccupancyTokens Occupancy) Assemble(
        EnvironmentPromptContext context,
        IReadOnlyList<IEnvironmentPromptSection> staticSections,
        IReadOnlyList<IEnvironmentPromptSection> preCallSections)
    {
        var rendered = new List<(IEnvironmentPromptSection Section, string Text)>();
        RenderLayer(context, staticSections, rendered);
        RenderLayer(context, preCallSections, rendered);

        var complete = rendered.Where(item => item.Section.IsComplete).ToArray();
        if (complete.Length > 1)
        {
            var names = string.Join(", ", complete.Select(item => item.Section.Name));
            throw new InvalidOperationException(
                $"Multiple complete prompt sections rendered non-empty content: {names}.");
        }

        IReadOnlyList<(IEnvironmentPromptSection Section, string Text)> effective =
            complete.Length == 1 ? complete : rendered;

        var builder = new StringBuilder();
        var occupancy = PromptOccupancyTokens.Empty;
        foreach (var item in effective)
        {
            var text = PromptVariableInterpolator.Interpolate(item.Text, context.Variables);
            if (!text.EndsWith('\n'))
            {
                text += Environment.NewLine;
            }

            builder.Append(text);
            occupancy = occupancy.Add(
                item.Section.OccupancyKind,
                ContextTokenEstimator.EstimateTextTokens(text));
        }

        return (builder.ToString().TrimEnd() + Environment.NewLine, occupancy);
    }

    private static void RenderLayer(
        EnvironmentPromptContext context,
        IReadOnlyList<IEnvironmentPromptSection> sections,
        List<(IEnvironmentPromptSection Section, string Text)> rendered)
    {
        foreach (var section in sections)
        {
            var buffer = new StringBuilder();
            section.Append(buffer, context);
            var text = buffer.ToString();
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            rendered.Add((section, text));
        }
    }
}
