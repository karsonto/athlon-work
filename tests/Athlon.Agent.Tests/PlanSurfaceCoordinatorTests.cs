using Athlon.Agent.App.Services;
using Athlon.Agent.Core.Plan;

namespace Athlon.Agent.Tests;

public sealed class PlanSurfaceCoordinatorTests
{
    [Fact]
    public void BuildApprovedPlanMessage_WithoutSeededList_ReturnsBasePrompt()
    {
        const string markdown = "## Plan\n\n1. Step one";
        var message = PlanSurfaceCoordinator.BuildApprovedPlanMessage(markdown, taskListSeeded: false);

        Assert.Equal(ApprovedPlanPrompt.BuildUserMessage(markdown), message);
        Assert.DoesNotContain("merge=true", message, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildApprovedPlanMessage_WithSeededList_AppendsMergeGuidance()
    {
        const string markdown = "## Plan";
        var message = PlanSurfaceCoordinator.BuildApprovedPlanMessage(markdown, taskListSeeded: true);

        Assert.StartsWith(ApprovedPlanPrompt.BuildUserMessage(markdown), message, StringComparison.Ordinal);
        Assert.Contains("merge=true", message, StringComparison.Ordinal);
        Assert.Contains("merge=false", message, StringComparison.Ordinal);
    }
}
