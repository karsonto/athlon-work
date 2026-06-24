using System.Text.RegularExpressions;

namespace Athlon.Agent.Infrastructure;

internal static class GrepLineMatcher
{
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(1);

    public static bool TryCreate(string pattern, bool useRegex, out GrepLineMatcherInstance? matcher, out string? errorMessage)
    {
        if (useRegex)
        {
            try
            {
                var regex = new Regex(
                    pattern,
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                    MatchTimeout);
                matcher = new RegexGrepLineMatcher(regex);
                errorMessage = null;
                return true;
            }
            catch (Exception ex) when (ex is ArgumentException or RegexMatchTimeoutException)
            {
                matcher = null;
                errorMessage = ex.Message;
                return false;
            }
        }

        matcher = new LiteralGrepLineMatcher(pattern);
        errorMessage = null;
        return true;
    }

    internal interface GrepLineMatcherInstance
    {
        bool IsMatch(string line);
    }

    private sealed class LiteralGrepLineMatcher(string pattern) : GrepLineMatcherInstance
    {
        public bool IsMatch(string line) => line.Contains(pattern, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class RegexGrepLineMatcher(Regex regex) : GrepLineMatcherInstance
    {
        public bool IsMatch(string line)
        {
            try
            {
                return regex.IsMatch(line);
            }
            catch (RegexMatchTimeoutException)
            {
                return false;
            }
        }
    }
}
