using Athlon.Agent.Core.ComposerCommands;

namespace Athlon.Agent.Infrastructure.ComposerCommands;

public sealed class ComposerCommandRegistry(IEnumerable<IComposerCommand> commands) : IComposerCommandRegistry
{
    private readonly Dictionary<string, IComposerCommand> _commands = commands
        .GroupBy(command => command.Descriptor.Name, StringComparer.OrdinalIgnoreCase)
        .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<ComposerCommandDescriptor> List() =>
        _commands.Values
            .Select(command => command.Descriptor)
            .OrderBy(descriptor => descriptor.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public bool TryGet(string name, out IComposerCommand? command) =>
        _commands.TryGetValue(name, out command);
}
