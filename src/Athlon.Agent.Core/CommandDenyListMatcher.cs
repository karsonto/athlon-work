using System.Text.RegularExpressions;

namespace Athlon.Agent.Core;

/// <summary>
/// Matches execute_command deny-list entries: multi-token phrases use substring match;
/// single tokens use word-boundary match to avoid false positives like "format" vs "formatting".
/// </summary>
public static class CommandDenyListMatcher
{
    public static bool IsDenied(string command, IEnumerable<string> denyList)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return false;
        }

        foreach (var deny in denyList)
        {
            if (string.IsNullOrWhiteSpace(deny))
            {
                continue;
            }

            if (Matches(command, deny.Trim()))
            {
                return true;
            }
        }

        return false;
    }

    private static bool Matches(string command, string deny)
    {
        if (deny.Contains(' ', StringComparison.Ordinal)
            || deny.Contains('\t', StringComparison.Ordinal))
        {
            return command.Contains(deny, StringComparison.OrdinalIgnoreCase);
        }

        try
        {
            return Regex.IsMatch(
                command,
                $@"\b{Regex.Escape(deny)}\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                TimeSpan.FromSeconds(1));
        }
        catch (RegexMatchTimeoutException)
        {
            return command.Contains(deny, StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return command.Contains(deny, StringComparison.OrdinalIgnoreCase);
        }
    }
}
