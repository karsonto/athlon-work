using Athlon.Agent.Core;

namespace Athlon.Agent.App.Services;

/// <summary>
/// Reconciles the provisional turn opener in the activity source against the durable transcript.
///
/// <para>The problem this solves: <see cref="SessionTurnUiController.AddUserMessage"/> appends the
/// user row the UI minted locally, but the runtime persists that turn's user message under its own
/// id. Until something with a reproducible id (a tool result, whose id derives from the tool call)
/// happens to be appended after it, that provisional row is the activity source's tail. Every merge
/// then looks its id up in the transcript, misses, and skips the whole continuation — which drops the
/// turn's final reply from the replay, since the activity source is what the replay renders. A turn
/// that calls no tools has nothing after the opener, so it always hit this.</para>
///
/// <para>Pairing is provable rather than positional: the runtime persists the user message with the
/// same input text the UI minted the provisional row from. When the source still holds an entry the
/// transcript also has, that entry anchors the search, so a mismatch leaves the source untouched
/// instead of binding to an unrelated turn's identical-looking message.</para>
///
/// <para>Deliberately pure and free of WPF types so the pairing rule is verifiable on its own — the
/// failure it guards against is silent, which makes an executable check of the rule worth more than
/// reasoning about it.</para>
/// </summary>
internal static class TurnOpenerReconciler
{
    /// <summary>
    /// Replaces a provisional turn opener at <paramref name="source"/>'s tail with the transcript's
    /// own user message, in place. Returns whether a replacement was made.
    /// </summary>
    public static bool TryAdoptProvisionalTurnOpener(
        List<ChatMessage> source,
        IReadOnlyList<ChatMessage> transcript)
    {
        if (source.Count == 0 || transcript.Count == 0)
        {
            return false;
        }

        var tail = source[^1];
        if (tail.Role != MessageRole.User)
        {
            return false;
        }

        // Already a transcript message (a rebuild from disk): nothing to reconcile.
        foreach (var message in transcript)
        {
            if (string.Equals(message.Id, tail.Id, StringComparison.Ordinal))
            {
                return false;
            }
        }

        if (source.Count > 1)
        {
            // The source's previous entry is the last message both sides agree on; this turn's
            // opener is the transcript's next user message after it, before any durable content.
            var previousId = source[^2].Id;
            var anchor = -1;
            for (var i = 0; i < transcript.Count; i++)
            {
                if (string.Equals(transcript[i].Id, previousId, StringComparison.Ordinal))
                {
                    anchor = i;
                    break;
                }
            }

            if (anchor < 0)
            {
                // The agreed prefix is gone from the transcript (compaction). Nothing to pair against.
                return false;
            }

            for (var i = anchor + 1; i < transcript.Count; i++)
            {
                var candidate = transcript[i];
                if (candidate.Role == MessageRole.User
                    && string.Equals(candidate.Content, tail.Content, StringComparison.Ordinal))
                {
                    source[^1] = candidate;
                    return true;
                }

                // Durable content before a matching opener means the transcript moved past this turn.
                // Compaction is deliberately not a stop: a checkpoint can sit between the anchor and
                // this turn's opener, and returning there would reintroduce the miss this prevents.
                if (candidate.Role is MessageRole.Assistant or MessageRole.Tool)
                {
                    return false;
                }
            }

            return false;
        }

        // No transcript-backed prefix to anchor on (a brand-new session's opening turn). Content is
        // the only signal left, so bind to the most recent match: the turn being finalized is the
        // newest one, whereas the first match would bind to an older turn that repeated the text.
        for (var i = transcript.Count - 1; i >= 0; i--)
        {
            var candidate = transcript[i];
            if (candidate.Role == MessageRole.User
                && string.Equals(candidate.Content, tail.Content, StringComparison.Ordinal))
            {
                source[^1] = candidate;
                return true;
            }
        }

        return false;
    }
}
