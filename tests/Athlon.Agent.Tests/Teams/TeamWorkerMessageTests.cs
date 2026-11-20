using Athlon.Agent.Core.Teams;
using Athlon.Agent.Infrastructure.Teams;

namespace Athlon.Agent.Tests.Teams;

public sealed class TeamWorkerMessageTests
{
    [Fact]
    public void BuildWorkerMessage_IncludesStructuredSections()
    {
        var task = new TeamTask
        {
            TaskId = "tm_1",
            Goal = "Ship the feature"
        };
        var sub = new TeamSubTask
        {
            Id = "sub_a",
            Title = "Implement",
            Iteration = 2,
            Handoff = "Touch Foo.cs",
            AcceptanceCriteria = "build green + unit tests",
            ArtifactRefs = ["src/Foo.cs", "tests/FooTests.cs"],
            PrevResultSummary = "previous attempt missed tests",
            RedispatchReason = "verification failed"
        };

        var message = TeamWorkerHelpers.BuildWorkerMessage(task, sub);

        Assert.Contains("## Goal", message);
        Assert.Contains("Ship the feature", message);
        Assert.Contains("## Handoff", message);
        Assert.Contains("Touch Foo.cs", message);
        Assert.Contains("## Acceptance", message);
        Assert.Contains("build green + unit tests", message);
        Assert.Contains("## Artifacts", message);
        Assert.Contains("src/Foo.cs", message);
        Assert.Contains("## PrevSummary", message);
        Assert.Contains("## Result format", message);
    }
}
