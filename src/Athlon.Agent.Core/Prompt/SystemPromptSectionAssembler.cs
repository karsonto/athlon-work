using System.Text;

namespace Athlon.Agent.Core.Prompt;

/// <summary>
/// Shared section assembly: duplicate-name check, empty-section drop, complete-section override, variable interpolation.
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

    public static string Assemble(
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

        string raw;
        if (complete.Length == 1)
        {
            raw = complete[0].Text;
        }
        else
        {
            var builder = new StringBuilder();
            foreach (var item in rendered)
            {
                builder.Append(item.Text);
                if (!item.Text.EndsWith('\n'))
                {
                    builder.AppendLine();
                }
            }

            raw = builder.ToString();
        }

        var interpolated = PromptVariableInterpolator.Interpolate(raw, context.Variables);
        return interpolated.TrimEnd() + Environment.NewLine;
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
