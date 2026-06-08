namespace Athlon.Agent.Core.ComposerCommands;

public interface IComposerCommandRegistry
{
    IReadOnlyList<ComposerCommandDescriptor> List();

    bool TryGet(string name, out IComposerCommand? command);
}
