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

    [Fact]
    public void FileEdit_many_chunks_emit_path_once_and_end_omits_edit_body()
    {
        var coordinator = new ToolCallArgsDisplayCoordinator();
        coordinator.MapForUi(new AgentStreamEvent.RunStarted("session", "run"));
        coordinator.MapForUi(new AgentStreamEvent.ToolCallStart("call-1", "file_edit", 0));

        var uiEvents = new List<AgentStreamEvent>();
        for (var i = 0; i < 8; i++)
        {
            var chunk = i switch
            {
                0 => """{"path":"src/App.cs","old_text":"a""",
                _ => """{"path":"src/App.cs","old_text":"abcdefghij","new_text":"ZZZZZZZZZZ"}"""
            };
            uiEvents.AddRange(coordinator.MapForUi(new AgentStreamEvent.ToolCallArgs("call-1", chunk)));
        }

        Assert.Single(uiEvents);
        var streamed = (AgentStreamEvent.ToolCallArgs)uiEvents[0];
        Assert.Equal(FileWriteToolArgumentsDisplay.FormatPath("src/App.cs"), streamed.Delta);
        Assert.DoesNotContain("old_text", streamed.Delta, StringComparison.Ordinal);
        Assert.DoesNotContain("new_text", streamed.Delta, StringComparison.Ordinal);

        var ended = coordinator.MapForUi(new AgentStreamEvent.ToolCallEnd("call-1"));
        var endArgs = (AgentStreamEvent.ToolCallArgs)ended[0];
        Assert.Equal(FileWriteToolArgumentsDisplay.FormatPath("src/App.cs"), endArgs.Delta);
        Assert.DoesNotContain("old_text", endArgs.Delta, StringComparison.Ordinal);
        Assert.DoesNotContain("abcdefghij", endArgs.Delta, StringComparison.Ordinal);
        Assert.IsType<AgentStreamEvent.ToolCallEnd>(ended[1]);
    }

    [Fact]
    public void ApplyPatch_many_chunks_emit_file_header_once_and_end_omits_patch_body()
    {
        var coordinator = new ToolCallArgsDisplayCoordinator();
        coordinator.MapForUi(new AgentStreamEvent.RunStarted("session", "run"));
        coordinator.MapForUi(new AgentStreamEvent.ToolCallStart("call-1", "apply_patch", 0));

        var header = """{"patch":"*** Begin Patch\n*** Update File: src/A.cs\n@@\n-old line\n+new line\n"}""";
        var grown = header + "\n*** End Patch trailing hunk that must stay off the timeline";
        var uiEvents = new List<AgentStreamEvent>();
        uiEvents.AddRange(coordinator.MapForUi(new AgentStreamEvent.ToolCallArgs("call-1", header)));
        uiEvents.AddRange(coordinator.MapForUi(new AgentStreamEvent.ToolCallArgs("call-1", grown)));
        uiEvents.AddRange(coordinator.MapForUi(new AgentStreamEvent.ToolCallArgs("call-1", grown + " more")));

        Assert.Single(uiEvents);
        Assert.Equal(FileWriteToolArgumentsDisplay.FormatPath("src/A.cs"), ((AgentStreamEvent.ToolCallArgs)uiEvents[0]).Delta);

        var ended = coordinator.MapForUi(new AgentStreamEvent.ToolCallEnd("call-1"));
        var endArgs = (AgentStreamEvent.ToolCallArgs)ended[0];
        Assert.Equal(FileWriteToolArgumentsDisplay.FormatPath("src/A.cs"), endArgs.Delta);
        Assert.DoesNotContain("old line", endArgs.Delta, StringComparison.Ordinal);
        Assert.DoesNotContain("trailing hunk", endArgs.Delta, StringComparison.Ordinal);
    }
}
