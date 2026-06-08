namespace Athlon.Agent.Core.ComposerCommands;

public sealed class ComposerCommandContext
{
    public required AgentSession Session { get; init; }

    public required string[] Args { get; init; }

    public required AppSettings Settings { get; init; }

    public AgentTurnCallbacks? Callbacks { get; init; }

    public CancellationToken CancellationToken { get; init; }
}
