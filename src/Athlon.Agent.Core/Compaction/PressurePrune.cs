namespace Athlon.Agent.Core.Compaction;

/// <summary>
/// Single entry for pressure-aware truncate/re-evict. Use <paramref name="persist"/> false for
/// Overflow retry outbound clones so aggressive prune does not rewrite session history.
/// </summary>
public static class PressurePrune
{
    public static int ResolvePreviewChars(ContextCompactionSettings settings, ContextPressureLevel pressure, bool retryOutbound)
    {
        var baseChars = Math.Max(256, settings.ToolResultEviction.PreviewChars);
        var divisor = pressure switch
        {
            ContextPressureLevel.Overflow when retryOutbound =>
                Math.Max(1, settings.OverflowRetryPreviewDivisor),
            ContextPressureLevel.Overflow =>
                Math.Max(1, settings.OverflowPreviewDivisor),
            ContextPressureLevel.Critical =>
                Math.Max(1, settings.CriticalPreviewDivisor),
            _ => 1
        };

        return Math.Max(128, baseChars / divisor);
    }

    public static int ResolveMaxArgLength(ContextCompactionSettings settings, ContextPressureLevel pressure)
    {
        if (pressure == ContextPressureLevel.Overflow)
        {
            return Math.Max(64, settings.OverflowMaxArgLength);
        }

        if (pressure == ContextPressureLevel.Critical)
        {
            return Math.Max(64, settings.TruncateArgs.MaxArgLength / Math.Max(1, settings.CriticalPreviewDivisor));
        }

        return settings.TruncateArgs.MaxArgLength;
    }

    public static (IReadOnlyList<ChatMessage> Messages, bool Changed) ApplyReEvict(
        IReadOnlyList<ChatMessage> messages,
        ContextCompactionSettings settings,
        int prefixCutoffExclusive,
        ContextPressureLevel pressure,
        bool retryOutbound = false)
    {
        var previewChars = ResolvePreviewChars(settings, pressure, retryOutbound);
        return PrefixToolResultReEvictor.Apply(messages, settings, prefixCutoffExclusive, previewChars);
    }
}
