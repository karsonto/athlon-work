using Athlon.Agent.Core;

namespace Athlon.Agent.Tests;

public sealed class SessionSkillActivationScopeTests
{
    [Fact]
    public void EnterNewTurn_TracksActivationAndCountsToolOutcomes()
    {
        using (SessionSkillActivationScope.EnterNewTurn())
        {
            var state = SessionSkillActivationScope.CurrentState;
            Assert.NotNull(state);

            state!.Activate("pdf-skill");
            state.Activate("pdf-skill");

            Assert.Equal("pdf-skill", state.LastActivatedSkillId);
            Assert.True(state.IsActive("pdf-skill"));
            Assert.Equal(0, state.TotalToolCalls);

            state.RecordToolOutcome(succeeded: true);
            state.RecordToolOutcome(succeeded: true);
            state.RecordToolOutcome(succeeded: false);

            Assert.Equal(3, state.TotalToolCalls);
            Assert.Equal(2, state.SucceededToolCalls);
            Assert.Equal(1, state.FailedToolCalls);
            Assert.Equal(state.SucceededToolCalls + state.FailedToolCalls, state.TotalToolCalls);
        }
    }

    [Fact]
    public void Activate_LastWins_WhenMultipleSkillsActive()
    {
        using (SessionSkillActivationScope.EnterNewTurn())
        {
            var state = SessionSkillActivationScope.CurrentState!;

            state.Activate("skill-a");
            state.Activate("skill-b");

            Assert.Equal("skill-b", state.LastActivatedSkillId);
            Assert.True(state.IsActive("skill-a"));
            Assert.True(state.IsActive("skill-b"));
        }
    }

    [Fact]
    public void EnterNewTurn_ResetsCountersForNextTurn()
    {
        using (SessionSkillActivationScope.EnterNewTurn())
        {
            var state = SessionSkillActivationScope.CurrentState!;
            state.Activate("pdf-skill");
            state.RecordToolOutcome(succeeded: true);
        }

        using (SessionSkillActivationScope.EnterNewTurn())
        {
            var state = SessionSkillActivationScope.CurrentState!;
            Assert.Null(state.LastActivatedSkillId);
            Assert.Equal(0, state.TotalToolCalls);
        }
    }

    [Fact]
    public void RecordToolOutcome_WithoutActivatedSkill_StillCountsWholeTurn()
    {
        // Whole-turn attribution: tool outcomes are counted even when they precede the
        // activation; emission is gated on LastActivatedSkillId at turn end.
        using (SessionSkillActivationScope.EnterNewTurn())
        {
            var state = SessionSkillActivationScope.CurrentState!;
            state.RecordToolOutcome(succeeded: true);
            state.RecordToolOutcome(succeeded: false);
            state.Activate("doc-skill");

            Assert.Equal("doc-skill", state.LastActivatedSkillId);
            Assert.Equal(2, state.TotalToolCalls);
            Assert.Equal(1, state.SucceededToolCalls);
            Assert.Equal(1, state.FailedToolCalls);
        }
    }
}
