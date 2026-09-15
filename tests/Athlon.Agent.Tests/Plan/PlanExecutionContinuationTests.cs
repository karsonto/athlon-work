using Athlon.Agent.App.Services;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Harness;
using Athlon.Agent.Core.Plan;

namespace Athlon.Agent.Tests.Plan;

public sealed class PlanContinuationTrackerTests
{
    [Fact]
    public void Increment_CountsPerSession_AndResetClears()
    {
        var tracker = new PlanContinuationTracker();

        Assert.Equal(0, tracker.GetCount("s1"));
        Assert.Equal(1, tracker.Increment("s1"));
        Assert.Equal(2, tracker.Increment("s1"));
        Assert.Equal(1, tracker.Increment("s2"));
        Assert.Equal(2, tracker.GetCount("s1"));

        tracker.Reset("s1");

        Assert.Equal(0, tracker.GetCount("s1"));
        Assert.Equal(1, tracker.GetCount("s2"));
    }

    [Fact]
    public void Stop_BlocksUntilReset()
    {
        var tracker = new PlanContinuationTracker();

        Assert.False(tracker.IsStopped("s1"));
        tracker.Stop("s1");
        Assert.True(tracker.IsStopped("s1"));

        tracker.Reset("s1");

        Assert.False(tracker.IsStopped("s1"));
    }

    [Fact]
    public void BlankSessionId_IsIgnored()
    {
        var tracker = new PlanContinuationTracker();

        tracker.Stop("  ");
        Assert.Equal(0, tracker.Increment(""));
        Assert.Equal(0, tracker.GetCount(""));
        Assert.False(tracker.IsStopped(""));
    }
}

public sealed class PlanExecutionContinuationPredicateTests
{
    [Theory]
    [InlineData(AgentTaskStatuses.Pending, true)]
    [InlineData(AgentTaskStatuses.InProgress, true)]
    [InlineData(AgentTaskStatuses.Completed, false)]
    [InlineData(AgentTaskStatuses.Cancelled, false)]
    public void HasOpenWork_OnlyCountsPendingAndInProgress(string status, bool expected)
    {
        var list = new SessionTaskList
        {
            Items = [new AgentTaskItem { Id = "1", Content = "task", Status = status }]
        };

        Assert.Equal(expected, PlanExecutionContinuationService.HasOpenWork(list));
    }

    [Fact]
    public void HasOpenWork_IsFalseForAnEmptyOrAllCompletedList()
    {
        Assert.False(PlanExecutionContinuationService.HasOpenWork(new SessionTaskList()));
        Assert.False(PlanExecutionContinuationService.HasOpenWork(new SessionTaskList
        {
            Items =
            [
                new AgentTaskItem { Id = "1", Content = "a", Status = AgentTaskStatuses.Completed },
                new AgentTaskItem { Id = "2", Content = "b", Status = AgentTaskStatuses.Cancelled }
            ]
        }));
    }
}

public sealed class AgentTurnPlanSettingsTests
{
    [Fact]
    public void PlanAutoContinue_DefaultsToEnabledWithTwelveRounds()
    {
        var settings = new AgentTurnSettings();

        Assert.True(settings.ResolvePlanAutoContinueEnabled());
        Assert.Equal(12, settings.ResolvePlanAutoContinueMaxRounds());
        Assert.True(settings.ResolveClearPlanOnAllTasksCompleted());
    }

    [Fact]
    public void PlanAutoContinueMaxRounds_IsClamped()
    {
        Assert.Equal(1, new AgentTurnSettings { PlanAutoContinueMaxRounds = 0 }.ResolvePlanAutoContinueMaxRounds());
        Assert.Equal(1, new AgentTurnSettings { PlanAutoContinueMaxRounds = -5 }.ResolvePlanAutoContinueMaxRounds());
        Assert.Equal(50, new AgentTurnSettings { PlanAutoContinueMaxRounds = 999 }.ResolvePlanAutoContinueMaxRounds());
        Assert.Equal(7, new AgentTurnSettings { PlanAutoContinueMaxRounds = 7 }.ResolvePlanAutoContinueMaxRounds());
    }

    [Fact]
    public void NullSettings_FallBackToDefaults()
    {
        AgentTurnSettings? settings = null;

        Assert.True(settings.ResolvePlanAutoContinueEnabled());
        Assert.Equal(12, settings.ResolvePlanAutoContinueMaxRounds());
        Assert.True(settings.ResolveClearPlanOnAllTasksCompleted());
    }
}

public sealed class PlanContinuePromptTests
{
    [Fact]
    public void BuildUserMessage_CarriesTheMarker()
    {
        var message = PlanContinuePrompt.BuildUserMessage();

        Assert.Contains(PlanContinuePrompt.Marker, message, StringComparison.Ordinal);
        Assert.Contains("todo_write", message, StringComparison.Ordinal);
    }

    [Fact]
    public void IsPlanContinueMessage_MatchesOnlyTheControlMessage()
    {
        var control = ChatMessage.Create(MessageRole.User, PlanContinuePrompt.BuildUserMessage());
        var userTurn = ChatMessage.Create(MessageRole.User, "please continue");
        var assistantTurn = ChatMessage.Create(MessageRole.Assistant, PlanContinuePrompt.Marker);

        Assert.True(PlanContinuePrompt.IsPlanContinueMessage(control));
        Assert.False(PlanContinuePrompt.IsPlanContinueMessage(userTurn));
        Assert.False(PlanContinuePrompt.IsPlanContinueMessage(assistantTurn));
    }
}
