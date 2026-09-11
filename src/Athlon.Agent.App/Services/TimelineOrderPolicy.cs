namespace Athlon.Agent.App.Services;

/// <summary>
/// Single source of truth for chat-timeline ordering.
///
/// Every rendered timeline entry carries a <c>seq</c> produced here, in both the live stream
/// and the replay projection. The web view sorts strictly by <c>seq</c> and never infers a
/// position from the DOM, so the same conversation renders identically whether it was just
/// streamed, restored on a session switch, or rebuilt after an app restart.
///
/// Ordering is banded per turn: each turn owns a fixed numeric range and the kinds inside it
/// occupy fixed sub-slots. An entry's seq is therefore a pure function of its turn and kind,
/// not of arrival order. That is what lets a streaming card (which arrives before the final
/// assistant reply) land in the same slot as the replayed card (which is emitted after it).
///
/// Layout inside a turn band:
/// <code>
///   +0          user message
///   +1_000      turn-activity fold (anchored before the turn's content bubbles)
///   +2_000..    content bubbles: tool cards and assistant replies, in transcript order
///   +800_000    files-changed card (summarizes the turn, so it follows the replies)
///   +900_000    compaction checkpoint
///   +950_000    plan-ready card (published by publish_plan; ends the turn)
/// </code>
/// </summary>
internal static class TimelineOrderPolicy
{
    /// <summary>Entries per turn band. Wide enough that a turn's ordinals never spill into the next.</summary>
    private const long TurnBand = 1_000_000L;

    private const long UserOffset = 0L;
    private const long ActivityOffset = 1_000L;
    private const long ContentOffset = 2_000L;
    private const long FilesOffset = 800_000L;
    private const long CompactionOffset = 900_000L;
    private const long PlanOffset = 950_000L;

    /// <summary>
    /// Upper bound for <paramref name="ordinal"/>. Content ordinals beyond this are clamped so a
    /// pathological turn can never collide with the files/compaction slots.
    /// </summary>
    private const int MaxContentOrdinal = 700_000;

    public static long User(long turnIndex) => Base(turnIndex) + UserOffset;

    /// <summary>
    /// Turn-activity fold. Emitted at a fixed slot before the turn's content so a live fold that
    /// is still growing occupies the same place as the fully rebuilt one.
    /// </summary>
    public static long Activity(long turnIndex) => Base(turnIndex) + ActivityOffset;

    /// <summary>Tool card or assistant reply, in transcript order within the turn.</summary>
    public static long Content(long turnIndex, int ordinal) =>
        Base(turnIndex) + ContentOffset + Math.Clamp(ordinal, 0, MaxContentOrdinal);

    public static long Files(long turnIndex) => Base(turnIndex) + FilesOffset;

    public static long Compaction(long turnIndex) => Base(turnIndex) + CompactionOffset;

    /// <summary>
    /// Plan-ready card. A <c>publish_plan</c> tool call ends the turn, so the run that produced it
    /// is pinned to that turn's band. The card used to float outside the timeline (no seq), which
    /// made it the one entry a full replay could not re-create; giving it a slot lets the replay
    /// projection and the live card agree on a position.
    /// </summary>
    public static long Plan(long turnIndex) => Base(turnIndex) + PlanOffset;

    private static long Base(long turnIndex) => Math.Max(0, turnIndex) * TurnBand;
}
