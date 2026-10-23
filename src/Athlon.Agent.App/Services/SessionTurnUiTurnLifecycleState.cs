namespace Athlon.Agent.App.Services;

public sealed class SessionTurnUiTurnLifecycleState
{
    public SessionTurnUiTurnPhase Phase { get; private set; } = SessionTurnUiTurnPhase.Resting;

    public string? TurnAnchorId { get; private set; }

    public int ActivityBlockIndex { get; private set; }

    public bool IsLive => Phase is SessionTurnUiTurnPhase.Live or SessionTurnUiTurnPhase.Finalizing;

    public void BeginLiveTurn(string? provisionalAnchorId = null)
    {
        Phase = SessionTurnUiTurnPhase.Live;
        if (!string.IsNullOrWhiteSpace(provisionalAnchorId))
        {
            SetTurnAnchor(provisionalAnchorId);
        }
    }

    public void SetTurnAnchor(string anchorId) => TurnAnchorId = anchorId;

    public void ClearTurnAnchor() => TurnAnchorId = null;

    public void ResetActivityBlockIndex() => ActivityBlockIndex = 0;

    public void IncrementActivityBlock() => ActivityBlockIndex++;

    public void EnterFinalizing() => Phase = SessionTurnUiTurnPhase.Finalizing;

    /// <summary>
    /// Marks the turn UI as resting. The turn anchor is retained so post-finalize replay and
    /// card re-publish keep the same entry keys as before lifecycle extraction.
    /// </summary>
    public void EnterResting() => Phase = SessionTurnUiTurnPhase.Resting;
}
