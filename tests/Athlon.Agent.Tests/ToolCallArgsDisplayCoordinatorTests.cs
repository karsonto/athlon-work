using Athlon.Agent.App.Services;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Streaming;

namespace Athlon.Agent.Tests;

public sealed class ToolCallArgsDisplayCoordinatorTests
{
    [Fact]
    public void FileWrite_many_chunks_emit_path_once_then_final_on_end()
    {
        var coordinator = new ToolCallArgsDisplayCoordinator();
        coordinator.MapForUi(new AgentStreamEvent.RunStarted("session", "run"));
        coordinator.MapForUi(new AgentStreamEvent.ToolCallStart("call-1", "file_write", 0));

        var uiEvents = new List<AgentStreamEvent>();
        for (var i = 0; i < 10; i++)
        {
            var chunk = i switch
            {
                0 => """{"path":"src/App.tsx","content":"a""",
                1 => """{"path":"src/App.tsx","content":"ab""",
                _ => """{"path":"src/App.tsx","content":"abc""",
            };
            uiEvents.AddRange(coordinator.MapForUi(new AgentStreamEvent.ToolCallArgs("call-1", chunk)));
        }

        uiEvents.AddRange(coordinator.MapForUi(new AgentStreamEvent.ToolCallEnd("call-1")));

        Assert.Equal(3, uiEvents.Count);
        Assert.IsType<AgentStreamEvent.ToolCallArgs>(uiEvents[0]);
        Assert.Contains("src/App.tsx", ((AgentStreamEvent.ToolCallArgs)uiEvents[0]).Delta, StringComparison.Ordinal);
        Assert.Contains(FileWriteToolArgumentsDisplay.StreamingContentLabel, ((AgentStreamEvent.ToolCallArgs)uiEvents[0]).Delta, StringComparison.Ordinal);
        Assert.Equal(
            Athlon.Agent.App.Resources.Strings.Get("FileWrite_ArgumentsJsonInvalid"),
            ((AgentStreamEvent.ToolCallArgs)uiEvents[1]).Delta);
        Assert.IsType<AgentStreamEvent.ToolCallEnd>(uiEvents[2]);
    }

    [Fact]
    public void Non_file_write_tool_passes_through_raw_args()
    {
        var coordinator = new ToolCallArgsDisplayCoordinator();
        coordinator.MapForUi(new AgentStreamEvent.RunStarted("session", "run"));
        coordinator.MapForUi(new AgentStreamEvent.ToolCallStart("call-1", "file_read", 0));

        var mapped = coordinator.MapForUi(new AgentStreamEvent.ToolCallArgs("call-1", """{"path":"README.md"}"""));

        Assert.Single(mapped);
        Assert.Equal("""{"path":"README.md"}""", ((AgentStreamEvent.ToolCallArgs)mapped[0]).Delta);
    }
}
