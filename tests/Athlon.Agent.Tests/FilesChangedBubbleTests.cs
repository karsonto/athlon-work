using System.Text.Json;
using Athlon.Agent.App.Services;
using Athlon.Agent.App.ViewModels;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Streaming;

namespace Athlon.Agent.Tests;

public sealed class FilesChangedBubbleTests
{
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
    public void BuildReplayEvents_emits_activity_bubble_above_each_model_output()
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
        Assert.Equal(2, activities.Count);
        Assert.Equal(2, texts.Count);
        Assert.True(events.IndexOf(activities[0]) < events.IndexOf(texts[0]));
        Assert.True(events.IndexOf(texts[0]) < events.IndexOf(activities[1]));
        Assert.True(events.IndexOf(activities[1]) < events.IndexOf(texts[1]));
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
    }
}
