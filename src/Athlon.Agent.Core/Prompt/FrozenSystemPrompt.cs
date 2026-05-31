namespace Athlon.Agent.Core.Prompt;

public sealed class FrozenSystemPrompt(string text)
{
    public string Text { get; } = text;
}
