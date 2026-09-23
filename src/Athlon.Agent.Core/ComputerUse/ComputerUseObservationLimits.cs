namespace Athlon.Agent.Core.ComputerUse;

/// <summary>
/// Shared bounds for <c>computer_observe</c> tree collection. The tool schema advertises these
/// values and the host clamps against them, so both sides stay in sync.
/// </summary>
public static class ComputerUseObservationLimits
{
    public const int MinTreeDepth = 1;
    public const int MaxTreeDepth = 10;
    public const int MinNodes = 20;
    public const int MaxNodes = 1000;
}

/// <summary>
/// Schema-facing defaults for Computer Use tools. Kept next to
/// <see cref="ComputerUseSettings"/> so the advertised defaults and the runtime settings agree;
/// a mismatch would let the model request a shape the host silently clamps.
/// </summary>
public static class ComputerUseSettingsDefaults
{
    public const int DefaultMaxTreeDepth = 3;

    public const int DefaultMaxNodes = 40;

    /// <summary>
    /// Stricter history-hygiene budget for a single Computer Use observation. Observations are large
    /// but short-lived: after the next action they only matter for continuity, so they should not
    /// consume the global 8000-token per-result allowance.
    /// </summary>
    public const int ObservationResultMaxLines = 120;

    public const int ObservationResultMaxBytes = 12 * 1024;

    public const int ObservationResultMaxTokens = 3_000;
}

/// <summary>Canonical Computer Use tool names, shared by tool registration and history hygiene.</summary>
public static class ComputerUseToolNames
{
    public const string Observe = "computer_observe";

    public const string Interact = "computer_interact";

    public const string Wait = "computer_wait";

    public static IReadOnlyList<string> All { get; } = [Observe, Interact, Wait];
}

/// <summary>
/// Lightweight metrics for serialized UI Automation trees, used for token baselines without
/// deserializing the payload.
/// </summary>
public static class ComputerUseUiTreeMetrics
{
    /// <summary>
    /// Counts nodes in a serialized node array. Falls back to a character-based estimate when the
    /// payload is not the expected shape or the serializer is unavailable.
    /// </summary>
    public static int CountNodes(string? uiTreeJson)
    {
        if (string.IsNullOrWhiteSpace(uiTreeJson))
        {
            return 0;
        }

        if (uiTreeJson.Length <= 2)
        {
            return 0;
        }

        var nodes = 0;
        var inString = false;
        var escaped = false;
        foreach (var ch in uiTreeJson)
        {
            if (escaped)
            {
                escaped = false;
                continue;
            }

            switch (ch)
            {
                case '\\' when inString:
                    escaped = true;
                    continue;
                case '"':
                    inString = !inString;
                    continue;
            }

            if (!inString && ch == '{')
            {
                nodes++;
            }
        }

        return nodes;
    }
}
