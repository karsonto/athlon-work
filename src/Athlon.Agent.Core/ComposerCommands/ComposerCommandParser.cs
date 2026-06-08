namespace Athlon.Agent.Core.ComposerCommands;

public static class ComposerCommandParser
{
    public static bool TryParse(string? input, out string command, out string[] args)
    {
        command = string.Empty;
        args = Array.Empty<string>();

        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        var trimmed = input.Trim();
        if (!trimmed.StartsWith('/', StringComparison.Ordinal))
        {
            return false;
        }

        var body = trimmed[1..].Trim();
        if (string.IsNullOrWhiteSpace(body))
        {
            return false;
        }

        var parts = body.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return false;
        }

        command = parts[0];
        args = parts.Length > 1 ? parts[1..] : Array.Empty<string>();
        return true;
    }
}
