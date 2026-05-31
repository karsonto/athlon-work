using Athlon.Agent.Core;
using Athlon.Agent.Core.Plan;

namespace Athlon.Agent.Tests;

public sealed class PlanAutoContinuePolicyTests
{
    [Fact]
    public void ShouldScheduleContinue_WhenNormalEndAndInProgress_ReturnsTrue()
    {
        var plan = CreatePlanWithInProgress();

        Assert.True(PlanAutoContinuePolicy.ShouldScheduleContinue(
            autoContinueEnabled: true,
            completedAutoContinueRounds: 0,
            maxRounds: 20,
            cancelled: false,
            timedOut: false,
            error: null,
            plan));
    }

    [Fact]
    public void ShouldScheduleContinue_WhenTimedOutAndInProgress_ReturnsTrue()
    {
        var plan = CreatePlanWithInProgress();

        Assert.True(PlanAutoContinuePolicy.ShouldScheduleContinue(
            autoContinueEnabled: true,
            completedAutoContinueRounds: 0,
            maxRounds: 20,
            cancelled: true,
            timedOut: true,
            error: null,
            plan));
    }

    [Fact]
    public void ShouldScheduleContinue_WhenUserCancelled_ReturnsFalse()
    {
        var plan = CreatePlanWithInProgress();

        Assert.False(PlanAutoContinuePolicy.ShouldScheduleContinue(
            autoContinueEnabled: true,
            completedAutoContinueRounds: 0,
            maxRounds: 20,
            cancelled: true,
            timedOut: false,
            error: null,
            plan));
    }

    [Fact]
    public void ShouldScheduleContinue_WhenError_ReturnsFalse()
    {
        var plan = CreatePlanWithInProgress();

        Assert.False(PlanAutoContinuePolicy.ShouldScheduleContinue(
            autoContinueEnabled: true,
            completedAutoContinueRounds: 0,
            maxRounds: 20,
            cancelled: false,
            timedOut: false,
            error: new InvalidOperationException("fail"),
            plan));
    }

    [Fact]
    public void ShouldScheduleContinue_WhenNoInProgress_ReturnsFalse()
    {
        var plan = new AgentPlan(
            "P",
            "D",
            "O",
            [new AgentSubTask("A", "", "") { State = SubTaskState.Done }]);

        Assert.False(PlanAutoContinuePolicy.ShouldScheduleContinue(
            autoContinueEnabled: true,
            completedAutoContinueRounds: 0,
            maxRounds: 20,
            cancelled: false,
            timedOut: false,
            error: null,
            plan));
    }

    [Fact]
    public void ShouldScheduleContinue_WhenMaxRoundsReached_ReturnsFalse()
    {
        var plan = CreatePlanWithInProgress();

        Assert.False(PlanAutoContinuePolicy.ShouldScheduleContinue(
            autoContinueEnabled: true,
            completedAutoContinueRounds: 20,
            maxRounds: 20,
            cancelled: false,
            timedOut: false,
            error: null,
            plan));
    }

    [Fact]
    public void ShouldScheduleContinue_WhenDisabled_ReturnsFalse()
    {
        var plan = CreatePlanWithInProgress();

        Assert.False(PlanAutoContinuePolicy.ShouldScheduleContinue(
            autoContinueEnabled: false,
            completedAutoContinueRounds: 0,
            maxRounds: 20,
            cancelled: false,
            timedOut: false,
            error: null,
            plan));
    }

    private static AgentPlan CreatePlanWithInProgress() =>
        new(
            "P",
            "D",
            "O",
            [new AgentSubTask("A", "Do A", "A done") { State = SubTaskState.InProgress }]);
}
