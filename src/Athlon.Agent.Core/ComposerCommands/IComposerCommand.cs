namespace Athlon.Agent.Core.ComposerCommands;

public interface IComposerCommand
{
    ComposerCommandDescriptor Descriptor { get; }

    Task<ComposerCommandResult> ExecuteAsync(ComposerCommandContext context);
}
