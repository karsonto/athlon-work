using Athlon.Agent.App.Services;
using Athlon.Agent.App.ViewModels;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Streaming;

namespace Athlon.Agent.Tests;

public sealed class SessionModifiedFilesTrackerTests
{
    [Fact]
    public void FileWriteToolCallArgs_adds_pending_file()
    {
        var tracker = new SessionModifiedFilesTracker();

        tracker.Process(new AgentStreamEvent.ToolCallStart("call-1", "file_write", 0));
        tracker.Process(new AgentStreamEvent.ToolCallArgs("call-1", """{"path":"src/App.tsx","content":"hello"}"""));

        Assert.Single(tracker.ModifiedFiles);
        Assert.Equal("src/App.tsx", tracker.ModifiedFiles[0].RelativePath);
        Assert.Equal(ModifiedFileStatus.Pending, tracker.ModifiedFiles[0].Status);
    }

    [Fact]
    public void FileEditToolCallResult_updates_status_to_succeeded()
    {
        var tracker = new SessionModifiedFilesTracker();
        var diff = string.Join(
            Environment.NewLine,
            "--- a/server.ts",
            "+++ b/server.ts",
            "@@ -1,1 +1,1 @@",
            "-a",
            "+b");
        var result = string.Join(
            Environment.NewLine,
            "ToolCallId: call-1",
            "Tool `file_edit` succeeded.",
            "",
            "Arguments: path=server.ts",
            "Summary: Edited server.ts (1 replacement(s))",
            "",
            diff);

        tracker.Process(new AgentStreamEvent.ToolCallStart("call-1", "file_edit", 0));
        tracker.Process(new AgentStreamEvent.ToolCallArgs("call-1", """{"path":"server.ts","old_text":"a","new_text":"b"}"""));
        tracker.Process(new AgentStreamEvent.ToolCallEnd("call-1"));
        tracker.Process(new AgentStreamEvent.ToolCallResult("call-1", result, "msg-1"));

        Assert.Single(tracker.ModifiedFiles);
        Assert.Equal("server.ts", tracker.ModifiedFiles[0].RelativePath);
        Assert.Equal(ModifiedFileStatus.Succeeded, tracker.ModifiedFiles[0].Status);
        Assert.True(tracker.ModifiedFiles[0].HasDiff);
        Assert.Equal(1, tracker.ModifiedFiles[0].AddedCount);
        Assert.Equal(1, tracker.ModifiedFiles[0].RemovedCount);
    }

    [Fact]
    public void TakeCurrentTurnSucceededFiles_returns_only_this_turn()
    {
        var tracker = new SessionModifiedFilesTracker();
        tracker.BeginTurn();
        tracker.Process(new AgentStreamEvent.ToolCallStart("call-1", "file_write", 0));
        tracker.Process(new AgentStreamEvent.ToolCallArgs("call-1", """{"path":"a.ts","content":"hello"}"""));
        tracker.Process(new AgentStreamEvent.ToolCallResult(
            "call-1",
            string.Join(
                Environment.NewLine,
                "ToolCallId: call-1",
                "Tool `file_write` succeeded.",
                "",
                "Arguments: path=a.ts",
                "Summary: Wrote 5 chars to a.ts",
                ""),
            "msg-1"));

        Assert.Single(tracker.TakeCurrentTurnSucceededFiles());

        tracker.BeginTurn();
        Assert.Empty(tracker.TakeCurrentTurnSucceededFiles());
        Assert.Single(tracker.ModifiedFiles);
    }

    [Fact]
    public void ApplyPatchResult_adds_multiple_files()
    {
        var tracker = new SessionModifiedFilesTracker();
        var result = string.Join(
            Environment.NewLine,
            "ToolCallId: call-2",
            "Tool `apply_patch` succeeded.",
            "",
            "Arguments: patch=...",
            "Summary: Patched 2 file(s)",
            "",
            "src/index.css",
            "src/App.tsx");

        tracker.Process(new AgentStreamEvent.ToolCallStart("call-2", "apply_patch", 0));
        tracker.Process(new AgentStreamEvent.ToolCallResult("call-2", result, "msg-2"));

        Assert.Equal(2, tracker.ModifiedFiles.Count);
        Assert.Contains(tracker.ModifiedFiles, file => file.RelativePath == "src/index.css");
        Assert.Contains(tracker.ModifiedFiles, file => file.RelativePath == "src/App.tsx");
        Assert.All(tracker.ModifiedFiles, file => Assert.Equal(ModifiedFileStatus.Succeeded, file.Status));
    }

    [Fact]
    public void SamePath_is_deduplicated_and_status_updated()
    {
        var tracker = new SessionModifiedFilesTracker();

        tracker.Process(new AgentStreamEvent.ToolCallStart("call-a", "file_write", 0));
        tracker.Process(new AgentStreamEvent.ToolCallArgs("call-a", """{"path":"package.json","content":"v1"}"""));
        tracker.Process(new AgentStreamEvent.ToolCallStart("call-b", "file_edit", 1));
        tracker.Process(new AgentStreamEvent.ToolCallArgs("call-b", """{"path":"package.json","old_text":"v1","new_text":"v2"}"""));

        Assert.Single(tracker.ModifiedFiles);

        var failedResult = string.Join(
            Environment.NewLine,
            "ToolCallId: call-b",
            "Tool `file_edit` failed.",
            "",
            "Arguments: path=package.json",
            "Summary: Text not found",
            "");

        tracker.Process(new AgentStreamEvent.ToolCallResult("call-b", failedResult, "msg-b"));

        Assert.Equal(ModifiedFileStatus.Failed, tracker.ModifiedFiles[0].Status);
    }

    [Fact]
    public void RebuildFromMessages_restores_completed_file_edits()
    {
        var tracker = new SessionModifiedFilesTracker();
        var content = string.Join(
            Environment.NewLine,
            "ToolCallId: call-1",
            "Tool `file_write` succeeded.",
            "",
            "Arguments: path = src/App.tsx; content = hello",
            "Summary: Wrote 5 chars to App.tsx",
            "");

        var messages = new List<ChatMessageViewModel>
        {
            new(ChatMessage.Create(MessageRole.Tool, content))
        };

        tracker.RebuildFromMessages(messages);

        Assert.Single(tracker.ModifiedFiles);
        Assert.Equal("src/App.tsx", tracker.ModifiedFiles[0].RelativePath);
        Assert.Equal(ModifiedFileStatus.Succeeded, tracker.ModifiedFiles[0].Status);
    }

    [Fact]
    public void FileWriteToolCallArgs_with_partial_json_still_extracts_path()
    {
        var tracker = new SessionModifiedFilesTracker();

        tracker.Process(new AgentStreamEvent.ToolCallStart("call-1", "file_write", 0));
        tracker.Process(new AgentStreamEvent.ToolCallArgs("call-1", """{"path":"x.ts","content":"abc"""));

        Assert.Single(tracker.ModifiedFiles);
        Assert.Equal("x.ts", tracker.ModifiedFiles[0].RelativePath);
    }

    [Theory]
    [InlineData("""{"path":"src/foo.ts"}""", "src/foo.ts")]
    [InlineData("""{"path":"x.ts","content":"abc""", "x.ts")]
    [InlineData("path = src/bar.ts\ncontent = hi", "src/bar.ts")]
    public void ExtractPathFromArguments_supports_json_and_persisted_formats(string input, string expected)
    {
        var path = ModifiedFilePathExtractor.ExtractPathFromArguments(input);
        Assert.Equal(expected, path);
    }
}
