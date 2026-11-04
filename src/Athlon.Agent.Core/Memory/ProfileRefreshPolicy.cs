using System.Collections.Concurrent;

namespace Athlon.Agent.Core.Memory;

/// <summary>
/// Decides whether this turn's flush should also rewrite <c>USER.md</c> / <c>LESSONS.md</c>.
///
/// The profile lives outside every session, so it must not be rewritten on every turn: that
/// would multiply the flush output by an order of magnitude and give the model a chance to drop
/// entries each time. Two independent signals gate the two documents:
///
/// <list type="bullet">
/// <item><c>USER.md</c> — time based. Missing/empty refreshes immediately, otherwise the document
/// stays fresh for <see cref="ProfileSettings.UserRefreshCooldown"/>.</item>
/// <item><c>LESSONS.md</c> — signal based. Only refreshes when a correction is newer than the
/// document's last write, so a session with no corrections never pays for it.</item>
/// </list>
///
/// No state file is written. Freshness comes from the document's own <c>UpdatedAt</c>, which works
/// for local files (mtime) and a future remote backend alike. The in-memory <c>_lastAttempt</c>
/// map only exists because an <c>UNCHANGED</c> answer writes nothing and would otherwise make the
/// same document due again on the very next turn. Losing it on restart costs one extra attempt.
/// </summary>
public sealed class ProfileRefreshPolicy(AppSettings settings)
{
    private readonly ConcurrentDictionary<ProfileKind, DateTimeOffset> _lastAttempt = new();

    /// <summary>Documents that should be included in this flush's prompt. Empty means "memory only".</summary>
    public IReadOnlyList<ProfileKind> GetDueKinds(
        ProfileDocument? user,
        ProfileDocument? lessons,
        DateTimeOffset? newestCorrectionAt,
        DateTimeOffset now)
    {
        var due = new List<ProfileKind>(ProfileKinds.All.Count);
        foreach (var kind in ProfileKinds.All)
        {
            if (IsDue(kind, kind == ProfileKind.User ? user : lessons, newestCorrectionAt, now))
            {
                due.Add(kind);
            }
        }

        return due;
    }

    /// <summary>
    /// Starts the cooldown for the documents this flush is about to ask the model about.
    /// Call before the LLM request: a failed call must not be retried on every subsequent turn.
    /// </summary>
    public void MarkAttempted(IEnumerable<ProfileKind> kinds, DateTimeOffset now)
    {
        foreach (var kind in kinds)
        {
            _lastAttempt[kind] = now;
        }
    }

    private bool IsDue(
        ProfileKind kind,
        ProfileDocument? document,
        DateTimeOffset? newestCorrectionAt,
        DateTimeOffset now)
    {
        var anchor = Later(document?.UpdatedAt, LastAttempt(kind));

        if (kind == ProfileKind.User)
        {
            return IsUserDue(document, anchor, now);
        }

        // Lessons are pure signal: without a correction newer than the last write, nothing to learn.
        return newestCorrectionAt is not null
            && (anchor is null || newestCorrectionAt > anchor);
    }

    private bool IsUserDue(ProfileDocument? document, DateTimeOffset? anchor, DateTimeOffset now)
    {
        if (document is null || document.IsEmpty)
        {
            return true;
        }

        return anchor is null || now - anchor.Value >= settings.Profile.UserRefreshCooldown;
    }

    private DateTimeOffset? LastAttempt(ProfileKind kind) =>
        _lastAttempt.TryGetValue(kind, out var at) ? at : null;

    private static DateTimeOffset? Later(DateTimeOffset? first, DateTimeOffset? second)
    {
        if (first is null)
        {
            return second;
        }

        if (second is null)
        {
            return first;
        }

        return first.Value >= second.Value ? first : second;
    }
}
