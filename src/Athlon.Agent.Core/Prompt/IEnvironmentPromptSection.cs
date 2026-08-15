using System.Text;

namespace Athlon.Agent.Core.Prompt;

public interface IEnvironmentPromptSection
{
    /// <summary>Stable diagnostic name (e.g. <c>athlon:identity</c>, <c>tool:files</c>). Must be unique per placement layer.</summary>
    string Name { get; }

    int Order { get; }

    PromptSectionPlacement Placement => PromptSectionPlacement.Static;

    /// <summary>
    /// When true and the section renders non-empty text, that text becomes the sole system prompt
    /// after assembly (variables still interpolate). Multiple non-empty complete sections reject assembly.
    /// </summary>
    bool IsComplete => false;

    PromptOccupancyKind OccupancyKind => PromptOccupancyKind.System;

    void Append(StringBuilder builder, EnvironmentPromptContext context);
}
