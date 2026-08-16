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
    public void SamePath_two_file_edits_accumulates_diff_counts()
    {
        var tracker = new SessionModifiedFilesTracker();
        ProcessSucceededFileEdit(
            tracker,
            "call-1",
            "src/SqliteUsageRecorder.java",
            string.Join(
                Environment.NewLine,
                "--- a/src/SqliteUsageRecorder.java",
                "+++ b/src/SqliteUsageRecorder.java",
                "@@ -1,0 +1,2 @@",
                "+line-a",
                "+line-b"));
        ProcessSucceededFileEdit(
            tracker,
            "call-2",
            "src/SqliteUsageRecorder.java",
            string.Join(
                Environment.NewLine,
                "--- a/src/SqliteUsageRecorder.java",
                "+++ b/src/SqliteUsageRecorder.java",
                "@@ -10,0 +10,1 @@",
                "+line-c"));

        Assert.Single(tracker.ModifiedFiles);
        var file = tracker.ModifiedFiles[0];
        Assert.Equal(3, file.AddedCount);
        Assert.Equal(0, file.RemovedCount);
        Assert.Contains("line-a", file.UnifiedDiffText, StringComparison.Ordinal);
        Assert.Contains("line-c", file.UnifiedDiffText, StringComparison.Ordinal);
    }

    [Fact]
    public void SamePath_two_file_writes_replaces_diff_counts()
    {
        var tracker = new SessionModifiedFilesTracker();
        ProcessSucceededFileWrite(tracker, "call-1", "a.ts", "one\ntwo\nthree");
        ProcessSucceededFileWrite(tracker, "call-2", "a.ts", "only");

        Assert.Single(tracker.ModifiedFiles);
        var file = tracker.ModifiedFiles[0];
        Assert.Equal(1, file.AddedCount);
        Assert.Equal(0, file.RemovedCount);
        Assert.DoesNotContain("two", file.UnifiedDiffText, StringComparison.Ordinal);
        Assert.DoesNotContain("three", file.UnifiedDiffText, StringComparison.Ordinal);
        Assert.Contains("only", file.UnifiedDiffText, StringComparison.Ordinal);
    }

    private static void ProcessSucceededFileEdit(
        SessionModifiedFilesTracker tracker,
        string toolCallId,
        string path,
        string diff)
    {
        var result = string.Join(
            Environment.NewLine,
            $"ToolCallId: {toolCallId}",
            "Tool `file_edit` succeeded.",
            "",
            $"Arguments: path={path}",
            "Summary: Edited",
            "",
            diff);

        tracker.Process(new AgentStreamEvent.ToolCallStart(toolCallId, "file_edit", 0));
        tracker.Process(new AgentStreamEvent.ToolCallArgs(
            toolCallId,
            $$"""{"path":"{{path}}","old_text":"x","new_text":"y"}"""));
        tracker.Process(new AgentStreamEvent.ToolCallEnd(toolCallId));
        tracker.Process(new AgentStreamEvent.ToolCallResult(toolCallId, result, $"msg-{toolCallId}"));
    }

    private static void ProcessSucceededFileWrite(
        SessionModifiedFilesTracker tracker,
        string toolCallId,
        string path,
        string content)
    {
        var escaped = content.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "");
        var args = $$"""{"path":"{{path}}","content":"{{escaped}}"}""";
        var result = string.Join(
            Environment.NewLine,
            $"ToolCallId: {toolCallId}",
            "Tool `file_write` succeeded.",
            "",
            $"Arguments: path={path}",
            "Summary: Wrote",
            "");

        tracker.Process(new AgentStreamEvent.ToolCallStart(toolCallId, "file_write", 0));
        tracker.Process(new AgentStreamEvent.ToolCallArgs(toolCallId, args));
        tracker.Process(new AgentStreamEvent.ToolCallEnd(toolCallId));
        tracker.Process(new AgentStreamEvent.ToolCallResult(toolCallId, result, $"msg-{toolCallId}"));
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
    public void TakeAndClearSegmentSucceededFiles_removes_paths_from_current_turn()
    {
        var tracker = new SessionModifiedFilesTracker();
        ProcessSucceededFileEdit(
            tracker,
            "call-1",
            "a.java",
            string.Join(
                Environment.NewLine,
                "--- a/a.java",
                "+++ b/a.java",
                "@@ -1,0 +1,1 @@",
                "+x"));
        ProcessSucceededFileEdit(
            tracker,
            "call-2",
            "b.java",
            string.Join(
                Environment.NewLine,
                "--- a/b.java",
                "+++ b/b.java",
                "@@ -1,0 +1,1 @@",
                "+y"));

        Assert.True(tracker.HasCurrentTurnPaths);
        var first = tracker.TakeAndClearSegmentSucceededFiles();
        Assert.Equal(2, first.Count);
        Assert.False(tracker.HasCurrentTurnPaths);
        Assert.Empty(tracker.TakeCurrentTurnSucceededFiles());

        ProcessSucceededFileEdit(
            tracker,
            "call-3",
            "c.java",
            string.Join(
                Environment.NewLine,
                "--- a/c.java",
                "+++ b/c.java",
                "@@ -1,0 +1,1 @@",
                "+z"));

        var second = tracker.TakeCurrentTurnSucceededFiles();
        Assert.Single(second);
        Assert.Equal("c.java", second[0].RelativePath);
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
