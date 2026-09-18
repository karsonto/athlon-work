using Athlon.Agent.App.Services;
using Athlon.Agent.App.ViewModels;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Streaming;

namespace Athlon.Agent.Tests;

public sealed class SessionModifiedFilesTrackerTests
{
    [Fact]
    public void FileWriteToolCallArgs_tracks_path_before_result()
    {
        var tracker = new SessionModifiedFilesTracker();

        tracker.Process(new AgentStreamEvent.ToolCallStart("call-1", "file_write", 0));
        tracker.Process(new AgentStreamEvent.ToolCallArgs("call-1", """{"path":"src/App.tsx","content":"hello"}"""));

        // A pending edit has no card yet, but it does mark the turn's live surface.
        Assert.True(tracker.HasCurrentTurnPaths);
        Assert.Empty(tracker.PeekSegmentEditCards());
    }

    [Fact]
    public void FileEditToolCallResult_stages_a_card_with_diff_counts()
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

        var card = Assert.Single(tracker.PeekSegmentEditCards());
        Assert.Equal("call-1", card.ToolCallId);
        var file = Assert.Single(card.Files);
        Assert.Equal("server.ts", file.RelativePath);
        Assert.True(file.HasDiff);
        Assert.Equal(1, file.AddedCount);
        Assert.Equal(1, file.RemovedCount);
    }

    [Fact]
    public void Two_edits_on_the_same_path_stage_one_card_each()
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

        // One independent card per edit, ordered by completion, instead of an aggregate per path.
        var cards = tracker.PeekSegmentEditCards();
        Assert.Equal(["call-1", "call-2"], cards.Select(card => card.ToolCallId));
        Assert.All(cards, card => Assert.Single(card.Files));
        Assert.All(cards, card => Assert.Equal("src/SqliteUsageRecorder.java", card.Files[0].RelativePath));

        var first = Assert.Single(cards[0].Files);
        Assert.Contains("line-a", first.UnifiedDiffText, StringComparison.Ordinal);
        Assert.DoesNotContain("line-c", first.UnifiedDiffText, StringComparison.Ordinal);

        var second = Assert.Single(cards[1].Files);
        Assert.Contains("line-c", second.UnifiedDiffText, StringComparison.Ordinal);
        Assert.DoesNotContain("line-a", second.UnifiedDiffText, StringComparison.Ordinal);
    }

    [Fact]
    public void Repeated_result_for_one_tool_call_replaces_its_card_instead_of_stacking()
    {
        var tracker = new SessionModifiedFilesTracker();
        ProcessSucceededFileWrite(tracker, "call-1", "a.ts", "one");
        ProcessSucceededFileWrite(tracker, "call-1", "a.ts", "two");

        var card = Assert.Single(tracker.PeekSegmentEditCards());
        Assert.Equal("call-1", card.ToolCallId);
        var file = Assert.Single(card.Files);
        Assert.Contains("two", file.UnifiedDiffText, StringComparison.Ordinal);
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
    public void ApplyPatchResult_stages_one_card_holding_every_touched_file()
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

        var card = Assert.Single(tracker.PeekSegmentEditCards());
        Assert.Equal("call-2", card.ToolCallId);
        Assert.Equal(2, card.Files.Count);
        Assert.Contains(card.Files, file => file.RelativePath == "src/index.css");
        Assert.Contains(card.Files, file => file.RelativePath == "src/App.tsx");
    }

    [Fact]
    public void Failed_result_stages_no_card_but_keeps_the_path_tracked()
    {
        var tracker = new SessionModifiedFilesTracker();

        tracker.Process(new AgentStreamEvent.ToolCallStart("call-a", "file_write", 0));
        tracker.Process(new AgentStreamEvent.ToolCallArgs("call-a", """{"path":"package.json","content":"v1"}"""));

        var failedResult = string.Join(
            Environment.NewLine,
            "ToolCallId: call-a",
            "Tool `file_write` failed.",
            "",
            "Arguments: path=package.json",
            "Summary: Permission denied",
            "");

        tracker.Process(new AgentStreamEvent.ToolCallResult("call-a", failedResult, "msg-a"));

        Assert.Empty(tracker.PeekSegmentEditCards());
        Assert.True(tracker.HasCurrentTurnPaths);
    }

    [Fact]
    public void BeginTurn_clears_prior_turn_file_state()
    {
        var tracker = new SessionModifiedFilesTracker();
        ProcessSucceededFileEdit(
            tracker,
            "call-1",
            "a.ts",
            string.Join(
                Environment.NewLine,
                "--- a/a.ts",
                "+++ b/a.ts",
                "@@ -1,1 +1,1 @@",
                "-a",
                "+b"));

        Assert.True(tracker.HasCurrentTurnPaths);
        Assert.Single(tracker.PeekSegmentEditCards());

        tracker.BeginTurn();

        Assert.False(tracker.HasCurrentTurnPaths);
        Assert.Empty(tracker.PeekSegmentEditCards());

        ProcessSucceededFileEdit(
            tracker,
            "call-2",
            "b.ts",
            string.Join(
                Environment.NewLine,
                "--- a/b.ts",
                "+++ b/b.ts",
                "@@ -1,1 +1,1 @@",
                "-x",
                "+y"));

        var card = Assert.Single(tracker.PeekSegmentEditCards());
        Assert.Equal("call-2", card.ToolCallId);
        Assert.Equal("b.ts", Assert.Single(card.Files).RelativePath);
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

        var card = Assert.Single(tracker.PeekSegmentEditCards());
        Assert.Equal("call-1", card.ToolCallId);
        var file = Assert.Single(card.Files);
        Assert.Equal("src/App.tsx", file.RelativePath);
        Assert.Equal(ModifiedFileStatus.Succeeded, file.Status);
    }

    [Fact]
    public void RebuildFromMessages_keeps_only_current_turn_edits()
    {
        static ChatMessageViewModel Edit(string id, string path) =>
            new(ChatMessage.Create(
                MessageRole.Tool,
                string.Join(
                    Environment.NewLine,
                    $"ToolCallId: {id}",
                    "Tool `file_edit` succeeded.",
                    "",
                    $"Arguments: path = {path}",
                    "Summary: Edited",
                    "",
                    $"--- a/{path}",
                    $"+++ b/{path}",
                    "@@ -1,1 +1,1 @@",
                    "-old",
                    "+new")));

        var messages = new List<ChatMessageViewModel>
        {
            new(ChatMessage.Create(MessageRole.User, "edit a")),
            Edit("call-1", "a.ts"),
            new(ChatMessage.Create(MessageRole.Assistant, "done a")),
            new(ChatMessage.Create(MessageRole.User, "edit b")),
            Edit("call-2", "b.ts"),
            new(ChatMessage.Create(MessageRole.Assistant, "done b"))
        };

        var tracker = new SessionModifiedFilesTracker();
        tracker.RebuildFromMessages(messages);

        // Only the last turn's edit is restorable; the earlier turn is owned by replay.
        var card = Assert.Single(tracker.PeekSegmentEditCards());
        Assert.Equal("call-2", card.ToolCallId);
        Assert.Equal("b.ts", Assert.Single(card.Files).RelativePath);
    }

    [Fact]
    public void RebuildFromMessages_drops_cards_for_a_turn_with_no_edits()
    {
        var tracker = new SessionModifiedFilesTracker();
        tracker.RebuildFromMessages(
        [
            new ChatMessageViewModel(ChatMessage.Create(MessageRole.User, "edit")),
            new ChatMessageViewModel(ChatMessage.Create(
                MessageRole.Tool,
                string.Join(
                    Environment.NewLine,
                    "ToolCallId: call-a",
                    "Tool `file_edit` succeeded.",
                    "",
                    "Arguments: path = a.ts",
                    "Summary: Edited a.ts",
                    "",
                    "--- a/a.ts",
                    "+++ b/a.ts",
                    "@@ -1,1 +1,1 @@",
                    "-old",
                    "+new")))
        ]);
        Assert.Single(tracker.PeekSegmentEditCards());

        tracker.RebuildFromMessages(
        [
            new ChatMessageViewModel(ChatMessage.Create(MessageRole.User, "just chatting"))
        ]);

        Assert.Empty(tracker.PeekSegmentEditCards());
        Assert.False(tracker.HasCurrentTurnPaths);
    }

    [Fact]
    public void IndexAfterLastTurnBoundary_returns_index_after_last_user()
    {
        var messages = new List<ChatMessageViewModel>
        {
            new(ChatMessage.Create(MessageRole.User, "one")),
            new(ChatMessage.Create(MessageRole.Assistant, "a")),
            new(ChatMessage.Create(MessageRole.User, "two")),
            new(ChatMessage.Create(MessageRole.Assistant, "b"))
        };

        Assert.Equal(3, SessionModifiedFilesTracker.IndexAfterLastTurnBoundary(messages));
    }

    [Fact]
    public void FileWriteToolCallArgs_with_partial_json_still_extracts_path()
    {
        var tracker = new SessionModifiedFilesTracker();

        tracker.Process(new AgentStreamEvent.ToolCallStart("call-1", "file_write", 0));
        tracker.Process(new AgentStreamEvent.ToolCallArgs("call-1", """{"path":"x.ts","content":"abc"""));

        Assert.True(tracker.HasCurrentTurnPaths);
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
