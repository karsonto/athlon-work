using System.Text.RegularExpressions;

namespace Athlon.Agent.App.Services.SlashCommands;

public sealed partial class ComposerSlashCommandExecutor
{
    private readonly IComposerSlashCommandRegistry _registry;

    public ComposerSlashCommandExecutor(IComposerSlashCommandRegistry registry)
    {
        _registry = registry;
    }

    public bool TryParseExactCommand(string composerText, out IComposerSlashCommand? command)
    {
        command = null;
        if (!TryGetExactCommandName(composerText, out var name))
        {
            return false;
        }

        return _registry.TryGetExact(name, out command);
    }

    public bool LooksLikeUnregisteredExactCommand(string composerText)
    {
        if (!TryGetExactCommandName(composerText, out var name))
        {
            return false;
        }

        return !_registry.TryGetExact(name, out _);
    }

    public async ValueTask<ComposerSlashCommandResult> ExecuteAsync(
        IComposerSlashCommand command,
        ComposerSlashCommandContext context,
        CancellationToken cancellationToken = default)
    {
        if (!command.IsAvailable(context))
        {
            return new ComposerSlashCommandResult(false, $"Command '/{command.Name}' is not available right now.");
        }

        return await command.ExecuteAsync(context, cancellationToken).ConfigureAwait(false);
    }

    private static bool TryGetExactCommandName(string composerText, out string name)
    {
        name = string.Empty;
        var trimmed = composerText.Trim();
        var match = ExactCommandPattern().Match(trimmed);
        if (!match.Success)
        {
            return false;
        }

        name = match.Groups[1].Value;
        return name.Length > 0;
    }

    [GeneratedRegex(@"^/([a-z][a-z0-9-]*)$", RegexOptions.CultureInvariant)]
    private static partial Regex ExactCommandPattern();
}
