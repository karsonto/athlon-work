using Athlon.Agent.Core;
using Athlon.Agent.Core.Compaction;
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

    [Fact]
    public async Task Flush_SkipsHandoffNote_AndDoesNotExtractIt()
    {
        var (service, client, memory) = CreateSut(llmContent: "- prefers tabs");
        var note = SessionHandoffNote.CreateMessage("Goal: finish the migration. Next: run tests.");

        var mixed = await service.FlushAsync(new MemoryTurnContext(
        [
            ChatMessage.Create(MessageRole.User, "I prefer tabs over spaces"),
            note
        ],
        "You are Athlon."));

        Assert.True(mixed.Flushed);
        Assert.DoesNotContain("finish the migration", client.LastPrompt, StringComparison.Ordinal);
        Assert.Contains("I prefer tabs over spaces", client.LastPrompt, StringComparison.Ordinal);

        var onlyNote = await service.FlushAsync(new MemoryTurnContext(
        [
            ChatMessage.Create(MessageRole.User, "<session_context>\nworkspace\n</session_context>"),
            note
        ],
        "You are Athlon."));

        Assert.False(onlyNote.Flushed);
        Assert.Single(memory.Appended);
    }

    private static MemoryTurnContext CreateExtractableContext() =>
        new(
        [
            ChatMessage.Create(MessageRole.User, "I prefer tabs over spaces"),
            ChatMessage.Create(MessageRole.Assistant, "Noted.")
        ],
        "You are Athlon.");

    [Fact]
    public async Task Flush_requests_user_section_when_profile_has_never_been_written()
    {
        var profile = new TestingProfileMemory();
        const string llmContent = """
## MEMORY
- prefers tabs

## USER
# USER

## Preferences
- wants replies in Chinese
""";
        var (service, client, memory) = CreateSut(
            llmContent,
            profile: profile,
            settings: new AppSettings());

        var result = await service.FlushAsync(CreateExtractableContext());

        Assert.True(result.Flushed);
        Assert.Contains("## USER", client.LastPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain("## LESSONS", client.LastPrompt, StringComparison.Ordinal);
        Assert.Equal(2048, client.LastRequest!.MaxTokens);

        Assert.Contains("- prefers tabs", Assert.Single(memory.Appended));
        var write = Assert.Single(profile.Writes);
        Assert.Equal(ProfileKind.User, write.Kind);
        Assert.Contains("wants replies in Chinese", write.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Flush_does_not_write_a_profile_document_when_the_model_says_unchanged()
    {
        var profile = new TestingProfileMemory()
            .Seed(ProfileKind.User, "# USER\n- prefers Chinese", DateTimeOffset.UtcNow.AddHours(-5));
        const string llmContent = """
## MEMORY
- prefers tabs

## USER
UNCHANGED
""";
        var (service, _, memory) = CreateSut(llmContent, profile: profile, settings: new AppSettings());

        await service.FlushAsync(CreateExtractableContext());

        Assert.Empty(profile.Writes);
        Assert.Single(memory.Appended);
    }

    [Fact]
    public async Task Flush_skips_the_profile_entirely_when_disabled()
    {
        var profile = new TestingProfileMemory();
        var settings = new AppSettings { Profile = { Enabled = false } };
        var (service, client, _) = CreateSut(profile: profile, settings: settings);

        await service.FlushAsync(CreateExtractableContext());

        Assert.DoesNotContain("## USER", client.LastPrompt, StringComparison.Ordinal);
        Assert.Empty(profile.Writes);
    }

    [Fact]
    public async Task Flush_requests_lessons_when_a_correction_is_newer_than_the_document()
    {
        var profile = new TestingProfileMemory()
            .Seed(ProfileKind.Lessons, "# LESSONS\n- old rule", DateTimeOffset.UtcNow.AddHours(-2));
        var settings = new AppSettings { Profile = { UserRefreshCooldown = TimeSpan.FromHours(6) } };
        profile.Seed(
            ProfileKind.User,
            "# USER\n- prefers Chinese",
            DateTimeOffset.UtcNow);
        const string llmContent = """
## MEMORY
NO_REPLY

## LESSONS
# LESSONS
- tool file_read failed on a binary file -> check the file type first
""";
        var (service, client, memory) = CreateSut(llmContent, profile: profile, settings: settings);

        var result = await service.FlushAsync(CreateCorrectionContext());

        Assert.Contains("## LESSONS", client.LastPrompt, StringComparison.Ordinal);
        Assert.Contains("New corrections:", client.LastPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain("## USER", client.LastPrompt, StringComparison.Ordinal);

        var write = Assert.Single(profile.Writes);
        Assert.Equal(ProfileKind.Lessons, write.Kind);
        Assert.Empty(memory.Appended);
        Assert.True(result.Flushed);
    }

    [Fact]
    public async Task Flush_does_not_ask_for_lessons_without_a_correction()
    {
        var profile = new TestingProfileMemory()
            .Seed(ProfileKind.Lessons, "# LESSONS\n- old rule", DateTimeOffset.UtcNow.AddDays(-1))
            .Seed(ProfileKind.User, "# USER\n- prefers Chinese", DateTimeOffset.UtcNow);
        var settings = new AppSettings { Profile = { UserRefreshCooldown = TimeSpan.FromHours(6) } };
        var (service, client, _) = CreateSut(profile: profile, settings: settings);

        await service.FlushAsync(CreateExtractableContext());

        Assert.DoesNotContain("## LESSONS", client.LastPrompt, StringComparison.Ordinal);
        Assert.Empty(profile.Writes);
    }

    private static MemoryTurnContext CreateCorrectionContext()
    {
        var failedCall = new AgentToolCall("call_1", "file_read", ToolCallArguments.Empty);
        var successCall = new AgentToolCall("call_2", "file_read", ToolCallArguments.Empty);
        return new MemoryTurnContext(
        [
            ChatMessage.Create(MessageRole.User, "read the config file"),
            ChatMessage.Create(MessageRole.Assistant, "Let me read it.", toolCalls: [failedCall]),
            ChatMessage.Create(MessageRole.Tool, "ToolCallId: call_1\nTool `file_read` failed.\nbinary file"),
            ChatMessage.Create(MessageRole.User, "它是二进制文件，先判断类型再读"),
            ChatMessage.Create(MessageRole.Assistant, "Understood.", toolCalls: [successCall]),
            ChatMessage.Create(MessageRole.Tool, "ToolCallId: call_2\nfile type detected")
        ],
        "You are Athlon.");
    }

    private static (MemoryFlushService Service, CapturingModelClient Client, RecordingLongTermMemory Memory) CreateSut(
        string llmContent = "- user prefers tabs",
        string curated = "User likes dark theme.",
        string daily = "",
        TestingProfileMemory? profile = null,
        AppSettings? settings = null,
        ProfileRefreshPolicy? policy = null)
    {
        var client = new CapturingModelClient(llmContent);
        var memory = new RecordingLongTermMemory(curated, daily);
        var effectiveSettings = settings ?? new AppSettings { Profile = { Enabled = false } };
        var effectiveProfile = profile ?? new TestingProfileMemory();
        var service = new MemoryFlushService(
            memory,
            client,
            new SessionUsageAccumulator(),
            new NoOpStorage(),
            new NoOpActiveAgentSessionContext(),
            effectiveSettings,
            effectiveProfile,
            policy ?? new ProfileRefreshPolicy(effectiveSettings),
            new NoOpLogger());
        return (service, client, memory);
    }

    /// <summary>Recording IProfileMemory used by the profile-extraction assertions.</summary>
    private sealed class TestingProfileMemory : IProfileMemory
    {
        private readonly Dictionary<ProfileKind, ProfileDocument> _documents = new();

        public ProfileCapabilities Capabilities { get; } =
            new(ProfileBackendKind.Local, CanWrite: true, CanDelete: true, IsRemote: false);

        public List<(ProfileKind Kind, string Content)> Writes { get; } = [];

        public TestingProfileMemory Seed(ProfileKind kind, string content, DateTimeOffset updatedAt)
        {
            _documents[kind] = new ProfileDocument(kind, content, updatedAt, ProfileBackendKind.Local);
            return this;
        }

        public Task<ProfileDocument?> ReadAsync(ProfileKind kind, CancellationToken cancellationToken = default) =>
            Task.FromResult(_documents.TryGetValue(kind, out var document) ? document : null);

        public Task WriteAsync(ProfileKind kind, string content, CancellationToken cancellationToken = default)
        {
            Writes.Add((kind, content));
            _documents[kind] = new ProfileDocument(kind, content, DateTimeOffset.UtcNow, ProfileBackendKind.Local);
            return Task.CompletedTask;
        }

        public Task<bool> DeleteAsync(ProfileKind kind, CancellationToken cancellationToken = default) =>
            Task.FromResult(_documents.Remove(kind));
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
