namespace Athlon.Agent.App.Services.Streaming;

/// <summary>
/// Coalesces streaming assistant-markdown posts so a slow WebView flush does not stack
/// redundant full-document Markdig renders. Finalize (<c>streaming: false</c>) always forces a
/// post once the in-flight work finishes (and clears any queued streaming snapshot).
/// </summary>
internal sealed class AssistantMarkdownCoalesceGate
{
    private readonly object _gate = new();
    private string? _lastMessageId;
    private string? _lastContent;
    private bool _inFlight;

    /// <summary>
    /// Decides whether a markdown post should run now, be skipped, or wait for the in-flight post.
    /// When the result is <see cref="CoalesceDecision.PostNow"/>, the caller must invoke
    /// <see cref="Complete"/> (or <see cref="Fail"/>) after the post finishes.
    /// </summary>
    public CoalesceDecision TryBegin(string messageId, string content, bool streaming)
    {
        lock (_gate)
        {
            content ??= string.Empty;
            messageId ??= string.Empty;

            if (streaming
                && string.Equals(_lastMessageId, messageId, StringComparison.Ordinal)
                && string.Equals(_lastContent, content, StringComparison.Ordinal))
            {
                return CoalesceDecision.SkipUnchanged;
            }

            if (_inFlight)
            {
                // Caller replaces its queued ViewModel snapshot (streaming or finalize).
                return CoalesceDecision.Queued;
            }

            _inFlight = true;
            _lastMessageId = messageId;
            _lastContent = content;
            return CoalesceDecision.PostNow;
        }
    }

    /// <summary>Marks the in-flight post finished so a queued follow-up may begin.</summary>
    public void Complete()
    {
        lock (_gate)
        {
            _inFlight = false;
        }
    }

    /// <summary>Same as <see cref="Complete"/> for error paths.</summary>
    public void Fail() => Complete();

    /// <summary>Test seam: whether a post is currently considered in flight.</summary>
    internal bool IsInFlight
    {
        get
        {
            lock (_gate)
            {
                return _inFlight;
            }
        }
    }

    /// <summary>Test seam: last content accepted for <see cref="CoalesceDecision.PostNow"/>.</summary>
    internal string? LastPostedContent
    {
        get
        {
            lock (_gate)
            {
                return _lastContent;
            }
        }
    }
}

internal enum CoalesceDecision
{
    PostNow,
    SkipUnchanged,
    Queued,
}
