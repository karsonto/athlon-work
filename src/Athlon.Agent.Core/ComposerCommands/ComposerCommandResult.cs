namespace Athlon.Agent.Core.ComposerCommands;

public sealed record ComposerCommandResult(
    ComposerCommandOutcome Outcome,
    AgentSession Session,
    string? StatusMessage);
