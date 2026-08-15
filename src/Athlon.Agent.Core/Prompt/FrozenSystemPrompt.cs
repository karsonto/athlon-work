namespace Athlon.Agent.Core.Prompt;

public sealed class FrozenSystemPrompt
{
    public FrozenSystemPrompt(string text, PromptOccupancyTokens? occupancy = null)
    {
        Text = text;
        Occupancy = occupancy ?? PromptOccupancyTokens.Empty;
    }

    public string Text { get; }

    public PromptOccupancyTokens Occupancy { get; }
}
