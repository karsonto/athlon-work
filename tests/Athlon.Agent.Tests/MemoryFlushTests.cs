using Athlon.Agent.Core;
using Athlon.Agent.Core.Memory;
using Athlon.Agent.Infrastructure.Memory;

namespace Athlon.Agent.Tests;

public sealed class MemoryFlushTests
{
    [Fact]
    public async Task Flush_PrefixReplay_ReusesEnvironmentPromptAndTools()
    {
        var tools = new[]
        {
            new ToolDefinition("file_read", "Read files", ToolSchema.Object().String("path", "path", required: true).Build())
        };
        const string environmentPrompt = "You are Athlon.";
        const string curated = "User likes dark theme.";
        const string daily = "This morning: chose SQLite.";
        var (service, client, memory) = CreateSut(llmContent: "- prefer tabs over spaces", curated, daily);

        var result = await service.FlushAsync(new MemoryTurnContext(
            [
                ChatMessage.Create(MessageRole.User, "I prefer tabs over spaces"),
                ChatMessage.Create(MessageRole.Assistant, "Noted.")
            ],
            environmentPrompt,
            tools));

        Assert.True(result.Flushed);
        Assert.NotNull(client.LastRequest);
        var request = client.LastRequest!;
        Assert.Equal("system", request.Messages[0].Role);
        Assert.Equal(environmentPrompt, request.Messages[0].Content as string);
        Assert.Equal("file_read", Assert.Single(request.Tools).Name);
        Assert.False(request.AllowToolCalls);
        Assert.Equal(1024, request.MaxTokens);

        var instruction = request.Messages[^1];
        Assert.Equal("user", instruction.Role);
        var instructionText = instruction.Content as string;
        Assert.Contains("preceding conversation", instructionText, StringComparison.Ordinal);
        Assert.Contains("MEMORY.md", instructionText, StringComparison.Ordinal);
        Assert.Contains(curated, instructionText, StringComparison.Ordinal);
        Assert.Contains(daily, instructionText, StringComparison.Ordinal);
        Assert.DoesNotContain("I prefer tabs over spaces", instructionText, StringComparison.Ordinal);
        Assert.Contains("I prefer tabs over spaces", client.LastPrompt, StringComparison.Ordinal);
        Assert.Contains("- prefer tabs over spaces", Assert.Single(memory.Appended));
    }

    [Fact]
    public async Task Flush_WithoutEnvironmentPrompt_UsesBlobFallback()
    {
        var (service, client, _) = CreateSut(llmContent: "- prefer tabs");

        await service.FlushAsync(new MemoryTurnContext(
        [
            ChatMessage.Create(MessageRole.User, "I prefer tabs over spaces"),
            ChatMessage.Create(MessageRole.Assistant, "Noted.")
        ]));

        Assert.NotNull(client.LastRequest);
        var request = client.LastRequest!;
        Assert.Equal("system", request.Messages[0].Role);
        Assert.Equal(MemoryFlushService.FlushSystemPrompt, request.Messages[0].Content as string);
        Assert.Empty(request.Tools);
        Assert.False(request.AllowToolCalls);
        Assert.Equal(2, request.Messages.Count);
        Assert.Equal("user", request.Messages[1].Role);
        var blob = request.Messages[1].Content as string;
        Assert.Contains("[User]: I prefer tabs over spaces", blob, StringComparison.Ordinal);
        Assert.Contains("Extract NEW memories from this conversation window", blob, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Flush_NoReply_DoesNotAppendDaily()
    {
        var (service, _, memory) = CreateSut(llmContent: "NO_REPLY");

        var result = await service.FlushAsync(CreateExtractableContext());

        Assert.False(result.Flushed);
        Assert.Empty(memory.Appended);
    }

    [Fact]
    public async Task Flush_EmptyModelOutput_DoesNotAppendDaily()
    {
        var (service, _, memory) = CreateSut(llmContent: "   ");

        var result = await service.FlushAsync(CreateExtractableContext());

        Assert.False(result.Flushed);
        Assert.Empty(memory.Appended);
    }

    [Fact]
    public async Task Flush_Skips_WhenNoExtractableConversation()
    {
        var (service, client, memory) = CreateSut();

        var result = await service.FlushAsync(new MemoryTurnContext(
        [
            ChatMessage.Create(MessageRole.System, "system"),
            ChatMessage.Create(MessageRole.Compaction, "old summary"),
            ChatMessage.Create(MessageRole.User, "<session_context>\nworkspace\n</session_context>")
        ],
        "You are Athlon."));

        Assert.False(result.Flushed);
        Assert.Null(client.LastRequest);
        Assert.Empty(memory.Appended);
        Assert.Equal(0, memory.ReadCuratedCount);
    }

    private static MemoryTurnContext CreateExtractableContext() =>
        new(
        [
            ChatMessage.Create(MessageRole.User, "I prefer tabs over spaces"),
            ChatMessage.Create(MessageRole.Assistant, "Noted.")
        ],
        "You are Athlon.");

    private static (MemoryFlushService Service, CapturingModelClient Client, RecordingLongTermMemory Memory) CreateSut(
        string llmContent = "- user prefers tabs",
        string curated = "User likes dark theme.",
        string daily = "")
    {
        var client = new CapturingModelClient(llmContent);
        var memory = new RecordingLongTermMemory(curated, daily);
        var service = new MemoryFlushService(
            memory,
            client,
            new SessionUsageAccumulator(),
            new NoOpStorage(),
            new NoOpActiveAgentSessionContext(),
            new AppSettings(),
            new NoOpLogger());
        return (service, client, memory);
    }

    private sealed class CapturingModelClient(string content) : IAgentModelClient
    {
        public AgentModelRequest? LastRequest { get; private set; }

        public string? LastPrompt { get; private set; }

        public Task<AgentModelResponse> CompleteAsync(
            AgentModelRequest request,
            Func<string, Task>? onTextDelta = null,
            Func<string, Task>? onReasoningDelta = null,
            Func<StreamingToolCallDelta, Task>? onToolCallDelta = null,
            CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            LastPrompt = string.Join(
                "\n",
                request.Messages.Select(message => message.Content as string ?? message.Content?.ToString() ?? string.Empty));
            return Task.FromResult(new AgentModelResponse(content, Array.Empty<AgentToolCall>()));
        }
    }

    private sealed class RecordingLongTermMemory(string curated, string daily) : ILongTermMemory
    {
        public List<string> Appended { get; } = [];
        public int ReadCuratedCount { get; private set; }

        public bool HasActiveScope => true;
        public string? ActiveWorkspaceKey => "ws";
        public string? ActiveSessionId => "sess";

        public Task<string> ReadCuratedAsync(CancellationToken cancellationToken = default)
        {
            ReadCuratedCount++;
            return Task.FromResult(curated);
        }

        public Task<string> ReadDailyAsync(DateTime date, CancellationToken cancellationToken = default) =>
            Task.FromResult(daily);

        public Task AppendDailyAsync(string text, CancellationToken cancellationToken = default)
        {
            Appended.Add(text);
            return Task.CompletedTask;
        }

        public Task<string> ReadDailyFileAsync(string fileName, CancellationToken cancellationToken = default) =>
            Task.FromResult("");

        public Task WriteCuratedAsync(string content, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<DateTime> ReadWatermarkAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(DateTime.MinValue);

        public Task WriteWatermarkAsync(DateTime watermark, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<string>> ListDailyFilesAfterAsync(DateTime after, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());

        public Task<IReadOnlyList<string>> ListAllMemoryFilePathsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>(["MEMORY.md"]);

        public Task ArchiveDailyFileAsync(string relativePath, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task DeleteCurrentSessionMemoryAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task DeleteSessionMemoryAsync(string? workspaceKey, string sessionId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
