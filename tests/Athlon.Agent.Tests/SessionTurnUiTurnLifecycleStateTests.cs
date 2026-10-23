using Athlon.Agent.App.Services;

namespace Athlon.Agent.Tests;

public sealed class SessionTurnUiTurnLifecycleStateTests
{
    [Fact]
    public void BeginLiveTurn_without_anchor_leaves_existing_anchor()
    {
        var state = new SessionTurnUiTurnLifecycleState();
        state.SetTurnAnchor("anchor-a");
        state.EnterResting();

        state.BeginLiveTurn();

        Assert.Equal(SessionTurnUiTurnPhase.Live, state.Phase);
        Assert.Equal("anchor-a", state.TurnAnchorId);
        Assert.True(state.IsLive);
    }

    [Fact]
    public void BeginLiveTurn_with_provisional_anchor_sets_anchor()
    {
        var state = new SessionTurnUiTurnLifecycleState();
        state.SetTurnAnchor("old");

        state.BeginLiveTurn("new");

        Assert.Equal(SessionTurnUiTurnPhase.Live, state.Phase);
        Assert.Equal("new", state.TurnAnchorId);
    }

    [Fact]
    public void Activity_block_index_resets_and_increments()
    {
        var state = new SessionTurnUiTurnLifecycleState();
        state.IncrementActivityBlock();
        state.IncrementActivityBlock();
        Assert.Equal(2, state.ActivityBlockIndex);

        state.ResetActivityBlockIndex();
        Assert.Equal(0, state.ActivityBlockIndex);
    }

    [Fact]
    public void Finalizing_and_resting_transitions()
    {
        var state = new SessionTurnUiTurnLifecycleState();
        state.BeginLiveTurn("turn-1");
        state.SetTurnAnchor("turn-1");

        state.EnterFinalizing();
        Assert.Equal(SessionTurnUiTurnPhase.Finalizing, state.Phase);
        Assert.True(state.IsLive);

        state.EnterResting();
        Assert.Equal(SessionTurnUiTurnPhase.Resting, state.Phase);
        Assert.False(state.IsLive);
        Assert.Equal("turn-1", state.TurnAnchorId);
    }

    [Fact]
    public void ClearTurnAnchor_clears_id()
    {
        var state = new SessionTurnUiTurnLifecycleState();
        state.SetTurnAnchor("x");
        state.ClearTurnAnchor();
        Assert.Null(state.TurnAnchorId);
    }
}
