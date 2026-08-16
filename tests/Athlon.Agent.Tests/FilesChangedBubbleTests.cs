using System.Text.Json;
using Athlon.Agent.App.Services;
using Athlon.Agent.App.ViewModels;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Compaction;
using Athlon.Agent.Core.Streaming;

namespace Athlon.Agent.Tests;

public sealed class FilesChangedBubbleTests
{
    [Fact]
    public void BuildReplayEvents_without_activity_source_loses_files_changed_when_tools_folded()
    {
        var user = ChatMessage.Create(MessageRole.User, "edit it");
        var edit = ChatMessage.Create(
            MessageRole.Tool,
            string.Join(
                Environment.NewLine,
                "ToolCallId: call-1",
                "Tool `file_edit` succeeded.",
                "",
                "Arguments: path = server.ts",
                "Summary: Edited",
                "",
                "--- a/server.ts",
                "+++ b/server.ts",
                "@@ -1,1 +1,1 @@",
                "-a",
                "+b"));
        var assistant = ChatMessage.Create(MessageRole.Assistant, "done");

        var displayOnly = new List<ChatMessageViewModel>
        {
            new(user),
            new(assistant)
        };

        var withoutSource = ChatEventSerializer.BuildReplayEvents(displayOnly, showToolCalls: false);
        Assert.DoesNotContain(withoutSource, json => json.Contains("FILES_CHANGED", StringComparison.Ordinal));

        var withSource = ChatEventSerializer.BuildReplayEvents(
            displayOnly,
            showToolCalls: false,
            activitySourceMessages: [user, edit, assistant]);
        Assert.Contains(withSource, json => json.Contains("FILES_CHANGED", StringComparison.Ordinal));
    }

    [Fact]
    public void SerializeFilesChanged_emits_independent_files_changed_event()
    {
        var file = new ModifiedFileViewModel("src/App.tsx", "file_edit", ModifiedFileStatus.Succeeded);
        file.SetDiff(string.Join(
            "\n",
            "--- a/src/App.tsx",
            "+++ b/src/App.tsx",
            "@@ -1,1 +1,1 @@",
            "-old",
            "+new"));

        var json = ChatEventSerializer.SerializeFilesChanged([file]);
        using var doc = JsonDocument.Parse(json);

        Assert.Equal("FILES_CHANGED", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal(1, doc.RootElement.GetProperty("files").GetArrayLength());
        Assert.Equal("src/App.tsx", doc.RootElement.GetProperty("files")[0].GetProperty("path").GetString());
        Assert.Equal(1, doc.RootElement.GetProperty("files")[0].GetProperty("added").GetInt32());
        Assert.Equal(1, doc.RootElement.GetProperty("files")[0].GetProperty("removed").GetInt32());
    }

    [Fact]
    public void BuildReplayEvents_emits_activity_and_independent_files_changed()
    {
        var user = ChatMessage.Create(MessageRole.User, "edit it");
        var edit = ChatMessage.Create(
            MessageRole.Tool,
            string.Join(
                Environment.NewLine,
                "ToolCallId: call-1",
                "Tool `file_edit` succeeded.",
                "",
                "Arguments: path = server.ts",
                "Summary: Edited server.ts (1 replacement(s))",
                "",
                "--- a/server.ts",
                "+++ b/server.ts",
                "@@ -1,1 +1,1 @@",
                "-a",
                "+b"));
        var read = ChatMessage.Create(
            MessageRole.Tool,
            string.Join(
                Environment.NewLine,
                "ToolCallId: call-2",
                "Tool `file_read` succeeded.",
                "",
                "Arguments: path = server.ts; start_line = 1; end_line = 20",
                "Summary: Read server.ts",
                "",
                "1|hello"));
        var list = ChatMessage.Create(
            MessageRole.Tool,
            string.Join(
                Environment.NewLine,
                "ToolCallId: call-3",
                "Tool `file_list` succeeded.",
                "",
                "Arguments: path = src",
                "Summary: Listed 4 entries",
                "",
                "a.cs"));
        var assistant = ChatMessage.Create(MessageRole.Assistant, "done");

        var display = new List<ChatMessageViewModel>
        {
            new(user),
            new(assistant)
        };
        var source = new List<ChatMessage> { user, edit, read, list, assistant };

        var events = ChatEventSerializer.BuildReplayEvents(display, showToolCalls: true, activitySourceMessages: source)
            .ToList();
        var activity = Assert.Single(events, json => json.Contains("TURN_ACTIVITY", StringComparison.Ordinal));
        var files = Assert.Single(events, json => json.Contains("FILES_CHANGED", StringComparison.Ordinal));
        var assistantHtml = Assert.Single(events, json => json.Contains("STATIC_ASSISTANT_HTML", StringComparison.Ordinal));

        Assert.True(
            events.IndexOf(activity) < events.IndexOf(assistantHtml),
            "Activity bubble should appear above the model text output.");
        Assert.True(
            events.IndexOf(files) < events.IndexOf(assistantHtml),
            "Files-changed bubble should appear above the model text output.");

        using var activityDoc = JsonDocument.Parse(activity);
        Assert.Equal(0, activityDoc.RootElement.GetProperty("editedFileCount").GetInt32());
        Assert.Equal(2, activityDoc.RootElement.GetProperty("exploredFileCount").GetInt32());
        Assert.DoesNotContain(
            activityDoc.RootElement.GetProperty("items").EnumerateArray(),
            item => item.GetProperty("kind").GetString() == "edited");

        using var filesDoc = JsonDocument.Parse(files);
        Assert.Equal(1, filesDoc.RootElement.GetProperty("files").GetArrayLength());
        Assert.Equal("server.ts", filesDoc.RootElement.GetProperty("files")[0].GetProperty("path").GetString());
    }

    [Fact]
    public void BuildReplayEvents_emits_single_activity_fold_for_whole_turn()
    {
        var user = ChatMessage.Create(MessageRole.User, "analyze");
        var read1 = ChatMessage.Create(
            MessageRole.Tool,
            string.Join(
                Environment.NewLine,
                "ToolCallId: c1",
                "Tool `file_read` succeeded.",
                "",
                "Arguments: path = a.ts",
                "Summary: Read a.ts",
                ""));
        var assistant1 = ChatMessage.Create(MessageRole.Assistant, "第一步");
        var read2 = ChatMessage.Create(
            MessageRole.Tool,
            string.Join(
                Environment.NewLine,
                "ToolCallId: c2",
                "Tool `file_read` succeeded.",
                "",
                "Arguments: path = b.ts",
                "Summary: Read b.ts",
                ""));
        var assistant2 = ChatMessage.Create(MessageRole.Assistant, "第二步");

        var source = new List<ChatMessage> { user, read1, assistant1, read2, assistant2 };
        var display = source.Select(message => new ChatMessageViewModel(message)).ToList();
        var events = ChatEventSerializer.BuildReplayEvents(display, showToolCalls: false, activitySourceMessages: source)
            .ToList();

        var activities = events.Where(json => json.Contains("TURN_ACTIVITY", StringComparison.Ordinal)).ToList();
        var texts = events.Where(json => json.Contains("STATIC_ASSISTANT_HTML", StringComparison.Ordinal)).ToList();
        Assert.Single(activities);
        Assert.Equal(1, texts.Count);
        Assert.True(events.IndexOf(activities[0]) < events.IndexOf(texts[0]));
        using var doc = JsonDocument.Parse(activities[0]);
        Assert.Equal(2, doc.RootElement.GetProperty("exploredFileCount").GetInt32());
        var kinds = doc.RootElement.GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("kind").GetString())
            .ToList();
        // Timeline order: read a.ts → narration「第一步」→ read b.ts (final「第二步」is bubble).
        Assert.Equal(["read", "narration", "read"], kinds);
    }

    [Fact]
    public void BuildReplayEvents_merges_tool_activity_across_empty_assistant_frames()
    {
        var user = ChatMessage.Create(MessageRole.User, "explore");
        static ChatMessage Read(string id, string path) => ChatMessage.Create(
            MessageRole.Tool,
            string.Join(
                Environment.NewLine,
                $"ToolCallId: {id}",
                "Tool `file_read` succeeded.",
                "",
                $"Arguments: path = {path}",
                $"Summary: Read {path}",
                ""));

        var empty = ChatMessage.Create(MessageRole.Assistant, "   ");
        var finalText = ChatMessage.Create(MessageRole.Assistant, "汇总完成");
        var source = new List<ChatMessage>
        {
            user,
            Read("c1", "a.ts"),
            empty,
            Read("c2", "b.ts"),
            Read("c3", "c.ts"),
            finalText
        };
        var display = source.Select(message => new ChatMessageViewModel(message)).ToList();
        var events = ChatEventSerializer.BuildReplayEvents(display, showToolCalls: false, activitySourceMessages: source)
            .ToList();

        var activities = events.Where(json => json.Contains("TURN_ACTIVITY", StringComparison.Ordinal)).ToList();
        Assert.Single(activities);
        using var doc = JsonDocument.Parse(activities[0]);
        Assert.Equal(3, doc.RootElement.GetProperty("exploredFileCount").GetInt32());
    }

    [Fact]
    public void TurnActivitySummaryBuilder_excludes_edits_from_activity()
    {
        var summary = TurnActivitySummaryBuilder.Build(
        [
            new ChatMessageViewModel(ChatMessage.Create(
                MessageRole.Tool,
                string.Join(
                    Environment.NewLine,
                    "ToolCallId: c1",
                    "Tool `file_write` succeeded.",
                    "",
                    "Arguments: path = a.ts; content = hello",
                    "Summary: Wrote 5 chars",
                    ""))),
            new ChatMessageViewModel(ChatMessage.Create(
                MessageRole.Tool,
                string.Join(
                    Environment.NewLine,
                    "ToolCallId: c2",
                    "Tool `file_read` succeeded.",
                    "",
                    "Arguments: path = a.ts; start_line = 1; end_line = 5",
                    "Summary: Read a.ts",
                    ""))),
            new ChatMessageViewModel(ChatMessage.Create(
                MessageRole.Tool,
                string.Join(
                    Environment.NewLine,
                    "ToolCallId: c3",
                    "Tool `grep_files` succeeded.",
                    "",
                    "Arguments: pattern = hello; path = .",
                    "Summary: Found 1",
                    "")))
        ]);

        Assert.NotNull(summary);
        Assert.Equal(0, summary!.EditedFileCount);
        Assert.Equal(1, summary.ExploredFileCount);
        Assert.Equal(1, summary.SearchCount);
        Assert.DoesNotContain(summary.Items, item => item.Kind == TurnActivityKind.Edited);
        Assert.Contains(summary.Items, item => item.Kind == TurnActivityKind.Read && item.Detail.Contains("L1-5", StringComparison.Ordinal));
        Assert.Contains(summary.Items, item => item.Kind == TurnActivityKind.Searched);
        Assert.All(
            summary.Items.Where(item => item.Kind != TurnActivityKind.Thought),
            item => Assert.Equal("succeeded", item.Status));
    }

    [Fact]
    public void SerializeTurnActivity_includes_item_status_labels()
    {
        var summary = new TurnActivitySummary
        {
            EditedFileCount = 0,
            ExploredFileCount = 1,
            SearchCount = 0,
            CommandCount = 0,
            ThoughtCount = 0,
            TotalAdded = 0,
            TotalRemoved = 0,
            Items =
            [
                new TurnActivityItem(TurnActivityKind.Read, "Read", "a.ts", "a.ts", Status: "succeeded"),
                new TurnActivityItem(TurnActivityKind.Explored, "Explored", "src", "src", Status: "failed")
            ]
        };

        var json = ChatEventSerializer.SerializeTurnActivity(summary);
        using var doc = JsonDocument.Parse(json);
        var items = doc.RootElement.GetProperty("items");
        Assert.Equal("succeeded", items[0].GetProperty("status").GetString());
        Assert.False(string.IsNullOrWhiteSpace(items[0].GetProperty("statusLabel").GetString()));
        Assert.Equal("failed", items[1].GetProperty("status").GetString());
    }

    [Fact]
    public void TurnActivitySummaryBuilder_includes_failed_tool_with_status()
    {
        var summary = TurnActivitySummaryBuilder.Build(
        [
            new ChatMessageViewModel(ChatMessage.Create(
                MessageRole.Tool,
                string.Join(
                    Environment.NewLine,
                    "ToolCallId: c1",
                    "Tool `file_read` failed.",
                    "",
                    "Arguments: path = missing.ts",
                    "Summary: File not found",
                    ""))),
            new ChatMessageViewModel(ChatMessage.Create(
                MessageRole.Tool,
                string.Join(
                    Environment.NewLine,
                    "ToolCallId: c2",
                    "Tool `file_list` succeeded.",
                    "",
                    "Arguments: path = src",
                    "Summary: Listed 4 entries",
                    "",
                    "a.cs")))
        ]);

        Assert.NotNull(summary);
        Assert.Equal(1, summary!.ExploredFileCount);
        Assert.Contains(summary.Items, item => item.Kind == TurnActivityKind.Read && item.Status == "failed");
        Assert.Contains(summary.Items, item => item.Kind == TurnActivityKind.Explored && item.Status == "succeeded");
    }

    [Fact]
    public void TurnActivitySummaryBuilder_folds_execute_command_with_status()
    {
        Assert.True(TurnActivityClassifier.IsActivityTool("execute_command"));

        var summary = TurnActivitySummaryBuilder.Build(
        [
            new ChatMessageViewModel(ChatMessage.Create(
                MessageRole.Tool,
                string.Join(
                    Environment.NewLine,
                    "ToolCallId: c1",
                    "Tool `execute_command` failed.",
                    "",
                    "Arguments: command = Get-Content missing.txt",
                    "Summary: Command failed",
                    ""))),
            new ChatMessageViewModel(ChatMessage.Create(
                MessageRole.Tool,
                string.Join(
                    Environment.NewLine,
                    "ToolCallId: c2",
                    "Tool `execute_command` succeeded.",
                    "",
                    "Arguments: command = Get-Content present.txt",
                    "Summary: Command succeeded",
                    "")))
        ]);

        Assert.NotNull(summary);
        Assert.Equal(2, summary!.CommandCount);
        Assert.Equal(2, summary.Items.Count(item => item.Kind == TurnActivityKind.Command));
        Assert.Contains(
            summary.Items,
            item => item.Status == "failed" && item.Detail.Contains("missing.txt", StringComparison.Ordinal));
        Assert.Contains(
            summary.Items,
            item => item.Status == "succeeded" && item.Detail.Contains("present.txt", StringComparison.Ordinal));
    }

    [Fact]
    public void TurnActivitySummaryBuilder_includes_reasoning_as_thought()
    {
        var summary = TurnActivitySummaryBuilder.Build(
        [
            new ChatMessageViewModel(ChatMessage.Create(
                MessageRole.Assistant,
                "done",
                reasoningContent: "先读文件，再改配置。")),
            new ChatMessageViewModel(ChatMessage.Create(
                MessageRole.Tool,
                string.Join(
                    Environment.NewLine,
                    "ToolCallId: c1",
                    "Tool `file_read` succeeded.",
                    "",
                    "Arguments: path = a.ts; start_line = 1; end_line = 5",
                    "Summary: Read a.ts",
                    "")))
        ]);

        Assert.NotNull(summary);
        Assert.Equal(1, summary!.ThoughtCount);
        Assert.Equal(1, summary.ExploredFileCount);
        var thought = Assert.Single(summary.Items, item => item.Kind == TurnActivityKind.Thought);
        Assert.Equal("先读文件，再改配置。", thought.Body);
        Assert.Contains("先读文件", thought.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildReplayEvents_compaction_splits_files_changed_without_path_overlap()
    {
        var user = ChatMessage.Create(MessageRole.User, "edit");
        var edit1 = ChatMessage.Create(
            MessageRole.Tool,
            string.Join(
                Environment.NewLine,
                "ToolCallId: call-1",
                "Tool `file_edit` succeeded.",
                "",
                "Arguments: path = a.java",
                "Summary: Edited",
                "",
                "--- a/a.java",
                "+++ b/a.java",
                "@@ -1,0 +1,1 @@",
                "+a"));
        var compaction = CompactionMessageContent.CreateCompactionMessage(
            CompactionMessageContent.CreateConversationCompact(
                1000, 500, 3, null, "summary", CompactionStrategy.ManualCompact));
        var edit2 = ChatMessage.Create(
            MessageRole.Tool,
            string.Join(
                Environment.NewLine,
                "ToolCallId: call-2",
                "Tool `file_edit` succeeded.",
                "",
                "Arguments: path = b.java",
                "Summary: Edited",
                "",
                "--- a/b.java",
                "+++ b/b.java",
                "@@ -1,0 +1,1 @@",
                "+b"));
        var assistant = ChatMessage.Create(MessageRole.Assistant, "done");

        var display = new List<ChatMessageViewModel>
        {
            new(user),
            new(assistant)
        };
        var source = new List<ChatMessage> { user, edit1, compaction, edit2, assistant };

        var events = ChatEventSerializer.BuildReplayEvents(display, showToolCalls: false, activitySourceMessages: source)
            .ToList();
        var fileEvents = events.Where(json => json.Contains("FILES_CHANGED", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, fileEvents.Count);

        using var first = JsonDocument.Parse(fileEvents[0]);
        using var second = JsonDocument.Parse(fileEvents[1]);
        Assert.Equal("a.java", first.RootElement.GetProperty("files")[0].GetProperty("path").GetString());
        Assert.Equal("b.java", second.RootElement.GetProperty("files")[0].GetProperty("path").GetString());
        Assert.Equal(1, first.RootElement.GetProperty("files").GetArrayLength());
        Assert.Equal(1, second.RootElement.GetProperty("files").GetArrayLength());
    }

    [Fact]
    public void BuildReplayEvents_folds_reasoning_into_turn_activity()
    {
        var user = ChatMessage.Create(MessageRole.User, "think");
        var assistant = ChatMessage.Create(
            MessageRole.Assistant,
            "ok",
            reasoningContent: "需要改 server.ts");
        var edit = ChatMessage.Create(
            MessageRole.Tool,
            string.Join(
                Environment.NewLine,
                "ToolCallId: call-1",
                "Tool `file_edit` succeeded.",
                "",
                "Arguments: path = server.ts",
                "Summary: Edited server.ts (1 replacement(s))",
                "",
                "--- a/server.ts",
                "+++ b/server.ts",
                "@@ -1,1 +1,1 @@",
                "-a",
                "+b"));

        var display = new List<ChatMessageViewModel>
        {
            new(user),
            new(assistant)
        };
        var source = new List<ChatMessage> { user, edit, assistant };

        var events = ChatEventSerializer.BuildReplayEvents(display, showToolCalls: true, activitySourceMessages: source)
            .ToList();
        Assert.DoesNotContain(events, json => json.Contains("REASONING_MESSAGE", StringComparison.Ordinal));
        var activity = Assert.Single(events, json => json.Contains("TURN_ACTIVITY", StringComparison.Ordinal));
        var files = Assert.Single(events, json => json.Contains("FILES_CHANGED", StringComparison.Ordinal));
        var assistantHtml = Assert.Single(events, json => json.Contains("STATIC_ASSISTANT_HTML", StringComparison.Ordinal));
        Assert.True(events.IndexOf(activity) < events.IndexOf(assistantHtml));
        Assert.True(events.IndexOf(files) < events.IndexOf(assistantHtml));
        using var activityDoc = JsonDocument.Parse(activity);
        Assert.Equal(1, activityDoc.RootElement.GetProperty("thoughtCount").GetInt32());
        Assert.Equal(0, activityDoc.RootElement.GetProperty("editedFileCount").GetInt32());
        using var filesDoc = JsonDocument.Parse(files);
        Assert.Equal(1, filesDoc.RootElement.GetProperty("files").GetArrayLength());
    }

    [Fact]
    public void SessionTurnActivityTracker_accumulates_live_thoughts()
    {
        var tracker = new SessionTurnActivityTracker();
        tracker.BeginTurn();
        tracker.Process(new AgentStreamEvent.ReasoningMessageStart("r1", "reasoning"));
        tracker.Process(new AgentStreamEvent.ReasoningMessageContent("r1", "分析路径"));
        tracker.Process(new AgentStreamEvent.ReasoningMessageEnd("r1"));

        var summary = tracker.Snapshot();
        Assert.NotNull(summary);
        Assert.Equal(1, summary!.ThoughtCount);
        Assert.Equal("分析路径", Assert.Single(summary.Items).Body);
        Assert.True(summary.DurationMs >= 0);
    }

    [Fact]
    public async Task SessionTurnActivityTracker_DurationMs_grows_until_BeginSegment_resets()
    {
        var tracker = new SessionTurnActivityTracker();
        tracker.BeginTurn();
        tracker.Process(new AgentStreamEvent.ReasoningMessageStart("r1", "reasoning"));
        tracker.Process(new AgentStreamEvent.ReasoningMessageContent("r1", "hello"));
        tracker.Process(new AgentStreamEvent.ReasoningMessageEnd("r1"));

        await Task.Delay(30);
        var first = tracker.Snapshot();
        Assert.NotNull(first);
        Assert.True(first!.DurationMs >= 20, $"expected DurationMs >= 20, got {first.DurationMs}");

        tracker.BeginSegment();
        tracker.Process(new AgentStreamEvent.ReasoningMessageStart("r2", "reasoning"));
        tracker.Process(new AgentStreamEvent.ReasoningMessageContent("r2", "next"));
        tracker.Process(new AgentStreamEvent.ReasoningMessageEnd("r2"));

        var second = tracker.Snapshot();
        Assert.NotNull(second);
        Assert.True(second!.DurationMs < first.DurationMs + 15, $"segment reset expected; first={first.DurationMs}, second={second.DurationMs}");
    }

    [Fact]
    public void SerializeTurnActivity_includes_durationMs()
    {
        var summary = new TurnActivitySummary
        {
            EditedFileCount = 0,
            ExploredFileCount = 1,
            SearchCount = 0,
            CommandCount = 0,
            ThoughtCount = 0,
            TotalAdded = 0,
            TotalRemoved = 0,
            DurationMs = 1500,
            Items =
            [
                new TurnActivityItem(TurnActivityKind.Read, "Read", "a.ts", "a.ts", Status: "succeeded")
            ]
        };

        var json = ChatEventSerializer.SerializeTurnActivity(summary, upsert: false);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(1500, doc.RootElement.GetProperty("durationMs").GetInt32());
        Assert.False(doc.RootElement.GetProperty("upsert").GetBoolean());
    }

    [Fact]
    public void SessionTurnActivityTracker_shows_in_progress_tools_before_result()
    {
        var tracker = new SessionTurnActivityTracker();
        tracker.BeginTurn();
        tracker.Process(new AgentStreamEvent.ToolCallStart("c1", "file_read", 0));
        tracker.Process(new AgentStreamEvent.ToolCallArgs("c1", """{"path":"a.ts","start_line":1,"end_line":5}"""));
        tracker.Process(new AgentStreamEvent.ToolCallEnd("c1"));
        tracker.Process(new AgentStreamEvent.ToolCallStart("c2", "execute_command", 1));
        tracker.Process(new AgentStreamEvent.ToolCallArgs("c2", """{"command":"Get-Content a.ts"}"""));
        tracker.Process(new AgentStreamEvent.ToolCallEnd("c2"));
        tracker.Process(new AgentStreamEvent.ToolCallStart("c3", "file_write", 2));
        tracker.Process(new AgentStreamEvent.ToolCallArgs("c3", """{"path":"b.ts","content":"x"}"""));
        tracker.Process(new AgentStreamEvent.ToolCallEnd("c3"));

        var summary = tracker.Snapshot();
        Assert.NotNull(summary);
        Assert.Contains(
            summary!.Items,
            item => item.Kind == TurnActivityKind.Read
                && item.Status == "running"
                && item.Verb == "Reading"
                && item.Detail.Contains("a.ts", StringComparison.Ordinal));
        Assert.Contains(
            summary.Items,
            item => item.Kind == TurnActivityKind.Command
                && item.Status == "running"
                && item.Verb == "Running"
                && item.Detail.Contains("Get-Content", StringComparison.Ordinal));
        Assert.Contains(
            summary.Items,
            item => item.Kind == TurnActivityKind.Edited
                && item.Status == "running"
                && item.Verb == "Writing"
                && item.Detail.Contains("b.ts", StringComparison.Ordinal));
        Assert.Equal(0, summary.ExploredFileCount);
    }

    [Fact]
    public void SessionTurnActivityTracker_result_replaces_pending_and_drops_successful_edit()
    {
        var tracker = new SessionTurnActivityTracker();
        tracker.BeginTurn();
        tracker.Process(new AgentStreamEvent.ToolCallStart("c1", "file_read", 0));
        tracker.Process(new AgentStreamEvent.ToolCallArgs("c1", """{"path":"a.ts"}"""));
        tracker.Process(new AgentStreamEvent.ToolCallEnd("c1"));
        tracker.Process(new AgentStreamEvent.ToolCallStart("c2", "file_write", 1));
        tracker.Process(new AgentStreamEvent.ToolCallArgs("c2", """{"path":"b.ts","content":"hi"}"""));
        tracker.Process(new AgentStreamEvent.ToolCallEnd("c2"));

        tracker.Process(new AgentStreamEvent.ToolCallResult(
            "c1",
            string.Join(
                Environment.NewLine,
                "ToolCallId: c1",
                "Tool `file_read` succeeded.",
                "",
                "Arguments: path = a.ts",
                "Summary: Read a.ts",
                ""),
            "m1"));
        tracker.Process(new AgentStreamEvent.ToolCallResult(
            "c2",
            string.Join(
                Environment.NewLine,
                "ToolCallId: c2",
                "Tool `file_write` succeeded.",
                "",
                "Arguments: path = b.ts; content = hi",
                "Summary: Wrote 2 chars",
                ""),
            "m2"));

        var summary = tracker.Snapshot();
        Assert.NotNull(summary);
        Assert.Contains(
            summary!.Items,
            item => item.Kind == TurnActivityKind.Read
                && item.Status == "succeeded"
                && item.Verb == "Read");
        Assert.DoesNotContain(summary.Items, item => item.Kind == TurnActivityKind.Edited);
        Assert.Equal(1, summary.ExploredFileCount);
    }

    [Fact]
    public void SessionTurnActivityTracker_keeps_failed_edit_in_activity()
    {
        var tracker = new SessionTurnActivityTracker();
        tracker.BeginTurn();
        tracker.Process(new AgentStreamEvent.ToolCallStart("c1", "file_write", 0));
        tracker.Process(new AgentStreamEvent.ToolCallArgs("c1", """{"path":"b.ts","content":"x"}"""));
        tracker.Process(new AgentStreamEvent.ToolCallEnd("c1"));
        tracker.Process(new AgentStreamEvent.ToolCallResult(
            "c1",
            string.Join(
                Environment.NewLine,
                "ToolCallId: c1",
                "Tool `file_write` failed.",
                "",
                "Arguments: path = b.ts",
                "Summary: Write failed",
                ""),
            "m1"));

        var summary = tracker.Snapshot();
        Assert.NotNull(summary);
        Assert.Contains(
            summary!.Items,
            item => item.Kind == TurnActivityKind.Edited
                && item.Status == "failed"
                && item.Detail.Contains("b.ts", StringComparison.Ordinal));
    }

    [Fact]
    public void TurnActivitySummaryBuilder_includes_in_progress_read_with_placeholder()
    {
        var pending = ChatMessageViewModel.CreatePendingTool(
            new AgentToolCall("c1", "file_read", ToolCallArguments.Empty));
        pending.ToolCallStatus = ToolCallDisplayStatus.Preparing;
        pending.IsToolRunning = false;

        var summary = TurnActivitySummaryBuilder.Build([pending]);
        Assert.NotNull(summary);
        var item = Assert.Single(summary!.Items);
        Assert.Equal(TurnActivityKind.Read, item.Kind);
        Assert.Equal("preparing", item.Status);
        Assert.Equal("Reading", item.Verb);
        Assert.Equal("…", item.Detail);
        Assert.Equal(0, summary.ExploredFileCount);
    }
}
