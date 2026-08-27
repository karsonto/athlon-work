using Athlon.Agent.App.Services;
using Athlon.Agent.App.ViewModels;
using Athlon.Agent.Core;
using Athlon.Agent.Infrastructure;

namespace Athlon.Agent.Tests;

public sealed class ToolDetailReadbackTests
{
    [Fact]
    public async Task LoadDisplayDetailAsync_reads_full_jsonl_body()
    {
        using var temp = new TempDirectoryScope("athlon-tool-detail");
        var paths = new TestPaths(temp.Root);
        var storage = new FileStorageService(new NoOpLogger(), paths, new JsonFileStore(), new AgentRunContextAccessor());
        var sessionId = "detail-session";
        await storage.SaveSessionAsync(AgentSession.Create(sessionId));

        var fullBody = string.Join(
            '\n',
            "ToolCallId: call-42",
            "Tool `file_read` succeeded.",
            "",
            "Arguments: path = note.txt",
            "Summary: Read note.txt",
            "",
            "hello world from file");
        var message = ChatMessage.Create(MessageRole.Tool, fullBody);
        await storage.AppendConversationMessageAsync(sessionId, message);

        var detail = await ToolDetailReadback.LoadDisplayDetailAsync(
            storage,
            sessionId,
            message.Id,
            "call-42");

        Assert.NotNull(detail);
        Assert.Contains("hello world from file", detail, StringComparison.Ordinal);
        Assert.Contains("Arguments:", detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LoadDisplayDetailAsync_resolves_evicted_archive()
    {
        using var temp = new TempDirectoryScope("athlon-tool-detail-evict");
        var paths = new TestPaths(temp.Root);
        var storage = new FileStorageService(new NoOpLogger(), paths, new JsonFileStore(), new AgentRunContextAccessor());
        var sessionId = "detail-evict-session";
        await storage.SaveSessionAsync(AgentSession.Create(sessionId));

        await storage.SaveEvictedToolResultAsync(sessionId, "call-evict", "EVICTED-FULL-OUTPUT");
        var placeholder = string.Join(
            '\n',
            "ToolCallId: call-evict",
            "Tool `execute_command` succeeded.",
            "",
            "Arguments: command = big",
            "Summary: ok",
            "",
            "[Tool result evicted - 99 chars]",
            "Archived at: somewhere",
            "Preview:",
            "abc");
        var message = ChatMessage.Create(MessageRole.Tool, placeholder);
        await storage.AppendConversationMessageAsync(sessionId, message);

        var detail = await ToolDetailReadback.LoadDisplayDetailAsync(
            storage,
            sessionId,
            message.Id,
            "call-evict");

        Assert.NotNull(detail);
        Assert.Contains("EVICTED-FULL-OUTPUT", detail, StringComparison.Ordinal);
    }

    [Fact]
    public void SerializeTurnActivity_includes_message_and_tool_ids()
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
                new TurnActivityItem(
                    TurnActivityKind.Read,
                    "Read",
                    "a.ts",
                    "a.ts",
                    Status: "succeeded",
                    MessageId: "msg-1",
                    ToolCallId: "call-1")
            ]
        };

        var json = ChatEventSerializer.SerializeTurnActivity(summary);
        Assert.Contains("\"messageId\":\"msg-1\"", json, StringComparison.Ordinal);
        Assert.Contains("\"toolCallId\":\"call-1\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void MaxToolDetailDisplayChars_is_raised_for_expand()
    {
        Assert.True(ChatMessageViewModel.MaxToolDetailDisplayChars >= 262_144);
        Assert.Equal(ToolDetailReadback.MaxDisplayChars, ChatMessageViewModel.MaxToolDetailDisplayChars);
    }

    private sealed class TestPaths(string root) : IAppPathProvider
    {
        public string RootPath { get; } = root;
        public string ConfigPath => Path.Combine(RootPath, "config");
        public string SessionsPath => Path.Combine(RootPath, "sessions");
        public string AuditPath => Path.Combine(RootPath, "audit");
        public string LogsPath => Path.Combine(RootPath, "logs");
        public string CredentialsPath => Path.Combine(RootPath, "credentials");
        public string SkillsPath => Path.Combine(RootPath, "skills");

        public void EnsureCreated() => Directory.CreateDirectory(RootPath);

        public string ResolveSkillPath(string path) =>
            string.IsNullOrWhiteSpace(path) ? path : Path.Combine(SkillsPath, path);
    }
}
