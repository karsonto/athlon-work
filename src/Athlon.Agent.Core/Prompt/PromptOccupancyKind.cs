using System.Text;
using Athlon.Agent.Core.Compaction;

namespace Athlon.Agent.Core.Prompt;

public enum PromptOccupancyKind
{
    System,
    Rules,
    Skills,
    Subagent
}

public sealed record PromptOccupancyTokens(
    int SystemPrompt = 0,
    int Rules = 0,
    int Skills = 0,
    int Subagent = 0)
{
    public static PromptOccupancyTokens Empty { get; } = new();

    public int Total => SystemPrompt + Rules + Skills + Subagent;

    public PromptOccupancyTokens Add(PromptOccupancyKind kind, int tokens)
    {
        if (tokens <= 0)
        {
            return this;
        }

        return kind switch
        {
            PromptOccupancyKind.Rules => this with { Rules = Rules + tokens },
            PromptOccupancyKind.Skills => this with { Skills = Skills + tokens },
            PromptOccupancyKind.Subagent => this with { Subagent = Subagent + tokens },
            _ => this with { SystemPrompt = SystemPrompt + tokens }
        };
    }

    public PromptOccupancyTokens Combine(PromptOccupancyTokens other) =>
        new(
            SystemPrompt + other.SystemPrompt,
            Rules + other.Rules,
            Skills + other.Skills,
            Subagent + other.Subagent);
}

public static class EnvironmentPromptOccupancy
{
    public static PromptOccupancyTokens AppendSections(
        StringBuilder builder,
        EnvironmentPromptContext context,
        IReadOnlyList<IEnvironmentPromptSection> sections)
    {
        var occupancy = PromptOccupancyTokens.Empty;
        foreach (var section in sections)
        {
            var start = builder.Length;
            section.Append(builder, context);
            if (builder.Length <= start)
            {
                continue;
            }

            var chunk = builder.ToString(start, builder.Length - start);
            occupancy = occupancy.Add(
                section.OccupancyKind,
                ContextTokenEstimator.EstimateTextTokens(chunk));
        }

        return occupancy;
    }
}
