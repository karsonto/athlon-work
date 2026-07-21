using Athlon.Agent.Core;
using Athlon.Agent.Core.Compaction;

namespace Athlon.Agent.Tests;

public sealed class ModelMessagesForApiBuilderTests
{
    [Fact]
    public void Build_applies_hygiene_to_oversized_tool_result()
    {
        var huge = new string('x', 50_000);
        var history = new List<ChatMessage>
        {
            ChatMessage.Create(MessageRole.User, "read file"),
            ChatMessage.CreateWithId(
                "a1",
                MessageRole.Assistant,
                string.Empty,
                null,
                [new AgentToolCall("tc1", "file_read", new Dictionary<string, string>())]),
            ChatMessage.Create(MessageRole.Tool, AgentRuntime.FormatToolResult(
                new AgentToolCall("tc1", "file_read", new Dictionary<string, string>()),
                ToolResult.Success("ok", huge)))
        };

        var result = ModelMessagesForApiBuilder.Build(
            cache: null,
            "system",
            history,
            new ContextCompactionSettings());

        var toolMessage = result.Messages.First(message => message.Role == "tool");
        var content = Assert.IsType<string>(toolMessage.Content);
        Assert.Contains("[cache hygiene:", content, StringComparison.Ordinal);
        Assert.True(result.EstimatedSavingsTokens > 0);
    }

    [Fact]
    public void Build_appends_runtime_context_without_mutating_cached_prefix()
    {
        var cache = new ModelMessageCache();
        var history = new[] { ChatMessage.Create(MessageRole.User, "question") };

        var first = ModelMessagesForApiBuilder.Build(
            cache,
            "stable system",
            history,
            new ContextCompactionSettings(),
            "runtime one");
        var second = ModelMessagesForApiBuilder.Build(
            cache,
            "stable system",
            history,
            new ContextCompactionSettings(),
            "runtime two");

        Assert.Equal(first.Messages.Count, second.Messages.Count);
        Assert.Equal("stable system", first.Messages[0].Content);
        Assert.Equal(first.Messages[0], second.Messages[0]);
        Assert.Equal("runtime one", first.Messages[^1].Content);
        Assert.Equal("runtime two", second.Messages[^1].Content);
        Assert.Single(first.Messages, message => Equals(message.Content, "runtime one"));
        Assert.Single(second.Messages, message => Equals(message.Content, "runtime two"));
    }

    [Fact]
    public void Build_sends_one_runtime_context_message_on_every_stateless_request()
    {
        var cache = new ModelMessageCache();
        var state = new RuntimeContextInjectionState();
        var history = new[] { ChatMessage.Create(MessageRole.User, "question") };
        var settings = new ContextCompactionSettings();

        var first = ModelMessagesForApiBuilder.Build(cache, "system", history, settings, "runtime one", state);
        var firstChanged = state.FingerprintChanged;
        var unchanged = ModelMessagesForApiBuilder.Build(cache, "system", history, settings, "runtime one", state);
        var unchangedChanged = state.FingerprintChanged;
        var changed = ModelMessagesForApiBuilder.Build(cache, "system", history, settings, "runtime two", state);

        Assert.Equal("runtime one", first.Messages[^1].Content);
        Assert.Equal("runtime one", unchanged.Messages[^1].Content);
        Assert.Equal("runtime two", changed.Messages[^1].Content);
        Assert.Single(unchanged.Messages, message => Equals(message.Content, "runtime one"));
        Assert.Equal("system", unchanged.Messages[0].Content);
        Assert.True(firstChanged);
        Assert.False(unchangedChanged);
        Assert.True(state.FingerprintChanged);
    }

    [Fact]
    public void Runtime_context_fingerprint_state_is_isolated_per_session_loop()
    {
        var history = new[] { ChatMessage.Create(MessageRole.User, "question") };
        var settings = new ContextCompactionSettings();
        var firstSessionState = new RuntimeContextInjectionState();
        var secondSessionState = new RuntimeContextInjectionState();

        _ = ModelMessagesForApiBuilder.Build(null, "system", history, settings, "shared", firstSessionState);
        var firstSessionRepeat = ModelMessagesForApiBuilder.Build(null, "system", history, settings, "shared", firstSessionState);
        var secondSessionFirst = ModelMessagesForApiBuilder.Build(null, "system", history, settings, "shared", secondSessionState);

        Assert.Equal("shared", firstSessionRepeat.Messages[^1].Content);
        Assert.Equal("shared", secondSessionFirst.Messages[^1].Content);
        Assert.False(firstSessionState.FingerprintChanged);
        Assert.True(secondSessionState.FingerprintChanged);
    }

    [Fact]
    public void Build_keeps_single_runtime_context_after_incremental_tool_loop_history()
    {
        var cache = new ModelMessageCache();
        var state = new RuntimeContextInjectionState();
        var toolCall = new AgentToolCall("call-1", "file_read", ToolCallArguments.Empty);
        var history = new List<ChatMessage> { ChatMessage.Create(MessageRole.User, "question") };
        _ = ModelMessagesForApiBuilder.Build(
            cache, "system", history, new ContextCompactionSettings(), "runtime", state);
        history.Add(ChatMessage.CreateWithId(
            "assistant-1", MessageRole.Assistant, string.Empty, null, [toolCall]));
        history.Add(ChatMessage.Create(
            MessageRole.Tool,
            AgentRuntime.FormatToolResult(toolCall, ToolResult.Success("read", "content"))));

        var next = ModelMessagesForApiBuilder.Build(
            cache, "system", history, new ContextCompactionSettings(), "runtime", state);

        Assert.Equal("runtime", next.Messages[^1].Content);
        Assert.Single(next.Messages, message => Equals(message.Content, "runtime"));
        Assert.Equal("tool", next.Messages[^2].Role);
    }

    [Fact]
    public void ModelMessageCache_ApplyHygiene_ReturnsIncrementalSavings()
    {
        var cache = new ModelMessageCache();
        var huge = new string('y', 40_000);
        var history = new List<ChatMessage>
        {
            ChatMessage.Create(MessageRole.User, "one"),
            ChatMessage.CreateWithId(
                "a1",
                MessageRole.Assistant,
                string.Empty,
                null,
                [new AgentToolCall("tc1", "file_read", new Dictionary<string, string>())]),
            ChatMessage.Create(
                MessageRole.Tool,
                AgentRuntime.FormatToolResult(
                    new AgentToolCall("tc1", "file_read", new Dictionary<string, string>()),
                    ToolResult.Success("ok", huge)))
        };

        cache.Build("sys", history, includeReasoningInModelContext: false);
        var first = cache.ApplyHygiene(new RequestHistoryHygieneSettings());
        Assert.True(first.EstimatedSavingsTokens > 0);

        history.Add(ChatMessage.Create(MessageRole.User, "two"));
        cache.Build("sys", history, includeReasoningInModelContext: false);
        var second = cache.ApplyHygiene(new RequestHistoryHygieneSettings());
        Assert.True(second.EstimatedSavingsTokens < first.EstimatedSavingsTokens);
    }

    [Fact]
    public void ModelMessageCache_NotePreCompletionResult_InvalidatesOnInPlaceContentChange()
    {
        var cache = new ModelMessageCache();
        var before = new List<ChatMessage>
        {
            ChatMessage.CreateWithId(
                "a1",
                MessageRole.Assistant,
                string.Empty,
                null,
                [new AgentToolCall("tc1", "file_write", new Dictionary<string, string> { ["content"] = new string('x', 100) })])
        };
        cache.Build("sys", before, includeReasoningInModelContext: false);

        var after = new TruncateArgsService().ApplyToMessages(
            before,
            new ContextCompactionSettings
            {
                TruncateArgs = new TruncateArgsSettings
                {
                    Enabled = true,
                    MaxArgLength = 10,
                    TruncationText = "...(truncated)",
                    TriggerMessages = 1
                }
            },
            out var changed,
            keepTokenBudgetOverride: 0);

        Assert.True(changed);
        cache.NotePreCompletionResult(before, after);
        // After invalidate, Build rebuilds from after history rather than appending stale prefix.
        var rebuilt = cache.Build("sys", after, includeReasoningInModelContext: false);
        var assistant = Assert.Single(rebuilt, message => message.Role == "assistant" && message.ToolCalls is { Count: > 0 });
        var contentArg = assistant.ToolCalls![0].Arguments["content"].GetString();
        Assert.Contains("...(truncated)", contentArg, StringComparison.Ordinal);
    }
}
