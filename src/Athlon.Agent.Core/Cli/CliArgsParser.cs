namespace Athlon.Agent.Core.Cli;

public static class CliArgsParser
{
    public static CliLaunchOptions Parse(IReadOnlyList<string> args)
    {
        var once = false;
        var yes = false;
        string? sessionId = null;
        var promptParts = new List<string>();

        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (string.Equals(arg, "--once", StringComparison.OrdinalIgnoreCase))
            {
                once = true;
                continue;
            }

            if (string.Equals(arg, "--yes", StringComparison.OrdinalIgnoreCase)
                || string.Equals(arg, "-y", StringComparison.OrdinalIgnoreCase))
            {
                yes = true;
                continue;
            }

            if (string.Equals(arg, "--session", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 < args.Count)
                {
                    sessionId = args[++i];
                }

                continue;
            }

            if (arg.StartsWith("--session=", StringComparison.OrdinalIgnoreCase))
            {
                sessionId = arg["--session=".Length..];
                continue;
            }

            promptParts.Add(arg);
        }

        var prompt = promptParts.Count == 0 ? null : string.Join(' ', promptParts);
        return new CliLaunchOptions(once, yes, sessionId, prompt);
    }
}

public static class CliReplCommand
{
    public static CliReplCommandKind Parse(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return CliReplCommandKind.Empty;
        }

        var trimmed = line.Trim();
        if (trimmed.Equals("/exit", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("/quit", StringComparison.OrdinalIgnoreCase))
        {
            return CliReplCommandKind.Exit;
        }

        if (trimmed.Equals("/new", StringComparison.OrdinalIgnoreCase))
        {
            return CliReplCommandKind.New;
        }

        return CliReplCommandKind.Message;
    }
}
