using Athlon.Agent.App.Resources;
using Athlon.Agent.Core.Compaction;

namespace Athlon.Agent.App.Services;

internal static class CompactionCheckpointCopy
{
    public static string FormatTitle(CompactionAuditDisplayInfo display, bool running)
    {
        if (running)
        {
            return Strings.Get("Chat_CompactionRunning");
        }

        var range = FormatTokenRange(display.TokensBefore, display.TokensAfter);
        if (display.Strategy == CompactionStrategy.ForceCompact)
        {
            return string.IsNullOrEmpty(range)
                ? Strings.Get("Chat_CompactionForceTitle")
                : Strings.Format("Chat_CompactionForceTitleWithTokens", range);
        }

        return string.IsNullOrEmpty(range)
            ? Strings.Get("Chat_CompactionCollapsedTitle")
            : Strings.Format("Chat_CompactionCollapsedTitleWithTokens", range);
    }

    public static string FormatTokenRange(int? tokensBefore, int? tokensAfter)
    {
        if (tokensBefore is not int before || tokensAfter is not int after)
        {
            return string.Empty;
        }

        return $"{TokenCountDisplay.FormatCompact(before)} → {TokenCountDisplay.FormatCompact(after)}";
    }
}
