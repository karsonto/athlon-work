namespace Athlon.Agent.App.Services.SlashCommands;

public sealed class ComposerSlashCommandRegistry : IComposerSlashCommandRegistry
{
    private readonly IReadOnlyList<IComposerSlashCommand> _commands;

    public ComposerSlashCommandRegistry(IEnumerable<IComposerSlashCommand> commands)
    {
        _commands = commands
            .OrderBy(command => command.Name, StringComparer.Ordinal)
            .ToArray();
    }

    public IReadOnlyList<IComposerSlashCommand> All => _commands;

    public bool TryGetExact(string name, out IComposerSlashCommand? command)
    {
        command = _commands.FirstOrDefault(item =>
            string.Equals(item.Name, name, StringComparison.Ordinal));
        return command is not null;
    }

    public IReadOnlyList<IComposerSlashCommand> MatchPrefix(string prefix, int maxItems)
    {
        if (maxItems <= 0)
        {
            return Array.Empty<IComposerSlashCommand>();
        }

        IEnumerable<IComposerSlashCommand> matches = string.IsNullOrEmpty(prefix)
            ? _commands
            : _commands.Where(command =>
                command.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                || command.Description.Contains(prefix, StringComparison.OrdinalIgnoreCase));

        return matches
            .OrderBy(command => command.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(command => command.Name, StringComparer.OrdinalIgnoreCase)
            .Take(maxItems)
            .ToArray();
    }
}
