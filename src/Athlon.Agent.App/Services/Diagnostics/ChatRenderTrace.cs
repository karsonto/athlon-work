using Athlon.Agent.Core;

namespace Athlon.Agent.App.Services.Diagnostics;

/// <summary>
/// Behavior-neutral observability for the chat timeline render pipeline.
///
/// <para>Written for the report "content sometimes only appears after switching session". Four
/// separate gates can each make a render quietly abandon, and every one of them was previously
/// invisible:</para>
///
/// <list type="number">
/// <item><c>CanRender()</c> false. The bare chat page shrinks the WebView to 2x2 while
/// <c>HasChatMessages</c> is false, and a session switch clears the message collection before
/// refilling it, so this gate is reachable during an ordinary switch. The old code reported it once
/// per false-&gt;true transition behind <c>_loggedCanRenderBlock</c>, so a gate that fired
/// repeatedly looked like it fired never.</item>
/// <item>Render-barrier timeout. <c>WaitForRenderGenerationAsync</c> gives up after 5s with no log
/// at all, which is indistinguishable from "the page simply never rendered".</item>
/// <item>Generation supersede. <c>StartRenderGeneration</c> invalidates in-flight work and the
/// invalidated side discards itself silently, without setting anything that would retry it.</item>
/// <item>Page-side parse or render failure. The timeline script's <c>catch</c> blocks only wrote to
/// <c>console.warn</c>, which nothing reads; a replay that ran and produced zero rows was likewise
/// unreported.</item>
/// </list>
///
/// <para>The decisive signal is <c>orphan</c>: the pipeline finished without rendering, a render is
/// still wanted, and nothing is scheduled to attempt it. Only an external event (resize,
/// visibility, a further request, or a session switch) clears that state — which is exactly why the
/// symptom reads as "switch session to make it appear". That state was previously unobservable by
/// construction.</para>
///
/// <para>Static because the call sites span <c>WebChatView</c> and
/// <c>SessionTurnUiController</c>; threading an instance through both would change public
/// signatures for diagnostics only. Mirrors <see cref="SessionSwitchProfiler"/>, which is the same
/// pattern for the switch path.</para>
///
/// <para>Every line ends with a running <c>#=</c> count. The report is "occasionally", so the
/// question that matters is how often a gate fired in total; a one-shot trace cannot answer it.</para>
/// </summary>
internal static class ChatRenderTrace
{
    /// <summary>Stable prefix so these lines are greppable apart from the session-switch ones.</summary>
    internal const string Prefix = "chat.render";

    private static readonly object Gate = new();
    private static readonly Dictionary<string, long> Counts = new(StringComparer.Ordinal);

    private static IAppLogger? _logger;
    private static bool _enabled = true;

    /// <summary>Wire the log sink. Safe to call once at startup.</summary>
    public static void Initialize(IAppLogger? logger, bool enabled = true)
    {
        lock (Gate)
        {
            _logger = logger;
            _enabled = enabled;
            Counts.Clear();
        }
    }

    /// <summary>Occurrences recorded for <paramref name="kind"/> (tests and diagnostics).</summary>
    public static long CountOf(string kind)
    {
        lock (Gate)
        {
            return Counts.GetValueOrDefault(kind);
        }
    }

    /// <summary>Drops counters without touching the sink. Used between test cases.</summary>
    public static void ResetCounts()
    {
        lock (Gate)
        {
            Counts.Clear();
        }
    }

    /// <summary>
    /// Records one observation. <paramref name="details"/> is a preformatted, greppable
    /// <c>key=value</c> fragment; pass null when the kind alone is the whole message.
    /// </summary>
    public static void Record(string kind, string? details = null)
    {
        if (string.IsNullOrEmpty(kind))
        {
            return;
        }

        long count;
        IAppLogger? logger;
        lock (Gate)
        {
            if (!_enabled)
            {
                return;
            }

            count = Counts.GetValueOrDefault(kind) + 1;
            Counts[kind] = count;
            logger = _logger;
        }

        if (logger is null)
        {
            return;
        }

        var line = string.IsNullOrEmpty(details)
            ? $"{Prefix}.{kind} #={count}"
            : $"{Prefix}.{kind} {details} #={count}";

        try
        {
            logger.Information("{Line}", line);
        }
        catch
        {
            // Instrumentation must never surface a failure into the render path.
        }
    }
}
