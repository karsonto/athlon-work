using Athlon.Agent.Core;
using Athlon.Agent.Core.Compaction;
using Athlon.Agent.Infrastructure;

namespace Athlon.Agent.Tests;

public sealed class BuildModelMessagesTests
{
    [Fact]
    public void BuildModelMessages_ToolScreenshot_AddsProviderCompatibleUserImageMessage()
    {
        var toolCallId = "call_observe";
        var call = new AgentToolCall(toolCallId, "computer_observe", ToolCallArguments.Empty);
        var toolContent = AgentRuntime.FormatToolResult(
            call,
            ToolResult.Success("observed", """{"frame_id":"frame_1"}"""));
        var history = new[]
        {
            ChatMessage.Create(MessageRole.User, "inspect desktop"),
            ChatMessage.Create(MessageRole.Assistant, string.Empty, toolCalls: [call]),
            ChatMessage.Create(
                MessageRole.Tool,
                toolContent,
                imageAttachments:
                [
                    new ImageAttachment(
                        "desktop.png",
                        "image/png",
                        DataUrl: "data:image/png;base64,AQID")
                ])
        };

        var messages = AgentRuntime.BuildModelMessages("system", history);

        Assert.Equal("tool", messages[^2].Role);
        Assert.Equal(toolCallId, messages[^2].ToolCallId);
        Assert.Equal("user", messages[^1].Role);
        var parts = Assert.IsAssignableFrom<IEnumerable<object>>(messages[^1].Content).ToArray();
        Assert.Equal(2, parts.Length);
        Assert.Contains("image_url", System.Text.Json.JsonSerializer.Serialize(parts[1]));

        var payload = OpenAiChatRequestFactory.BuildPayload(
            new AgentModelRequest(messages, []),
            new AppSettings(),
            stream: false);
        using var document = System.Text.Json.JsonDocument.Parse(
            System.Text.Json.JsonSerializer.Serialize(payload["messages"]));
        var providerMessages = document.RootElement.EnumerateArray().ToArray();
        Assert.Equal("assistant", providerMessages[^3].GetProperty("role").GetString());
        Assert.True(providerMessages[^3].TryGetProperty("tool_calls", out _));
        Assert.Equal("tool", providerMessages[^2].GetProperty("role").GetString());
        Assert.Equal(toolCallId, providerMessages[^2].GetProperty("tool_call_id").GetString());
        Assert.Equal("user", providerMessages[^1].GetProperty("role").GetString());
        Assert.Equal(
            "image_url",
            providerMessages[^1].GetProperty("content")[1].GetProperty("type").GetString());
    }

    [Fact]
    public void BuildModelMessages_AssistantWithToolCalls_FollowedByTool_IsValidForApi()
    {
        var toolCallId = "call_abc";
        var history = new[]
        {
            ChatMessage.Create(MessageRole.User, "list files"),
            ChatMessage.Create(
                MessageRole.Assistant,
                string.Empty,
                toolCalls: new[] { new AgentToolCall(toolCallId, "file_list", new Dictionary<string, string>()) }),
            ChatMessage.Create(
                MessageRole.Tool,
                AgentRuntime.FormatToolResult(
                    new AgentToolCall(toolCallId, "file_list", new Dictionary<string, string>()),
                    ToolResult.Success("ok", "file-a.txt")))
        };

        var messages = AgentRuntime.BuildModelMessages("system", history);

        Assert.Equal(4, messages.Count);
        Assert.Equal("assistant", messages[2].Role);
        Assert.NotNull(messages[2].ToolCalls);
        Assert.Single(messages[2].ToolCalls!);
        Assert.Equal("tool", messages[3].Role);
        Assert.Equal(toolCallId, messages[3].ToolCallId);
    }

    [Fact]
    public void BuildModelMessages_OrphanToolMessage_EmitsAsUserFallback()
    {
        var history = new[]
        {
            ChatMessage.Create(MessageRole.User, "hello"),
            ChatMessage.Create(MessageRole.Tool, "Tool `grep` succeeded.\n\nSummary: ok")
        };

        var messages = AgentRuntime.BuildModelMessages("system", history);

        Assert.Equal(3, messages.Count);
        Assert.Equal("user", messages[2].Role);
        Assert.Contains("[Tool output]", messages[2].Content.ToString()!, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildModelMessages_MultipleToolCalls_EachGetsToolMessage()
    {
        var callA = "call_a";
        var callB = "call_b";
        var toolCalls = new[]
        {
            new AgentToolCall(callA, "file_read", new Dictionary<string, string> { ["path"] = "a.txt" }),
            new AgentToolCall(callB, "file_read", new Dictionary<string, string> { ["path"] = "b.txt" })
        };
        var history = new[]
        {
            ChatMessage.Create(MessageRole.User, "read both"),
            ChatMessage.Create(MessageRole.Assistant, string.Empty, toolCalls: toolCalls),
            ChatMessage.Create(
                MessageRole.Tool,
                AgentRuntime.FormatToolResult(toolCalls[0], ToolResult.Success("ok", "a"))),
            ChatMessage.Create(
                MessageRole.Tool,
                AgentRuntime.FormatToolResult(toolCalls[1], ToolResult.Success("ok", "b")))
        };

        var messages = AgentRuntime.BuildModelMessages("system", history);

        Assert.Equal(5, messages.Count);
        Assert.Equal(2, messages[2].ToolCalls!.Count);
        Assert.Equal("tool", messages[3].Role);
        Assert.Equal(callA, messages[3].ToolCallId);
        Assert.Equal("tool", messages[4].Role);
        Assert.Equal(callB, messages[4].ToolCallId);
    }

    [Fact]
    public void BuildModelMessages_MissingToolResult_UsesPlaceholderToolMessage()
    {
        var callA = "call_a";
        var callB = "call_b";
        var toolCalls = new[]
        {
            new AgentToolCall(callA, "file_read", new Dictionary<string, string>()),
            new AgentToolCall(callB, "grep_files", new Dictionary<string, string>())
        };
        var history = new[]
        {
            ChatMessage.Create(MessageRole.User, "run"),
            ChatMessage.Create(MessageRole.Assistant, string.Empty, toolCalls: toolCalls),
            ChatMessage.Create(
                MessageRole.Tool,
                AgentRuntime.FormatToolResult(toolCalls[0], ToolResult.Success("ok", "only a")))
        };

        var messages = AgentRuntime.BuildModelMessages("system", history);

        Assert.Equal(5, messages.Count);
        Assert.Equal(2, messages[2].ToolCalls!.Count);
        Assert.Equal("tool", messages[3].Role);
        Assert.Equal(callA, messages[3].ToolCallId);
        Assert.Equal("tool", messages[4].Role);
        Assert.Equal(callB, messages[4].ToolCallId);
        Assert.Contains("not recorded", messages[4].Content.ToString()!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildModelMessages_AssistantWithReasoningContent_OmittedByDefault()
    {
        var history = new[]
        {
            ChatMessage.Create(MessageRole.User, "question"),
            ChatMessage.Create(MessageRole.Assistant, "answer", reasoningContent: "thinking chain")
        };

        var messages = AgentRuntime.BuildModelMessages("system", history);

        Assert.Equal(3, messages.Count);
        Assert.Equal("assistant", messages[2].Role);
        Assert.Null(messages[2].ReasoningContent);
    }

    [Fact]
    public void BuildModelMessages_AssistantWithReasoningContent_PassesThroughWhenEnabled()
    {
        var history = new[]
        {
            ChatMessage.Create(MessageRole.User, "question"),
            ChatMessage.Create(MessageRole.Assistant, "answer", reasoningContent: "thinking chain")
        };

        var messages = AgentRuntime.BuildModelMessages("system", history, includeReasoningInModelContext: true);

        Assert.Equal(3, messages.Count);
        Assert.Equal("assistant", messages[2].Role);
        Assert.Equal("thinking chain", messages[2].ReasoningContent);
    }

    [Fact]
    public void BuildModelMessages_RetainsOnlyLatestTwoToolScreenshots()
    {
        var history = new List<ChatMessage>
        {
            ChatMessage.Create(MessageRole.User, "open browser")
        };
        AppendComputerObserveTurn(history, "call_1", "data:image/png;base64,AA==");
        AppendComputerObserveTurn(history, "call_2", "data:image/png;base64,AQ==");
        AppendComputerObserveTurn(history, "call_3", "data:image/png;base64,Ag==");

        var result = ModelMessagesForApiBuilder.Build(
            cache: null,
            "system",
            history,
            new ContextCompactionSettings { MaxToolScreenshotsInModelContext = 2 });
        var imageUrls = CollectImageUrls(result.Messages);

        Assert.Equal(2, imageUrls.Count);
        Assert.Contains("data:image/png;base64,AQ==", imageUrls);
        Assert.Contains("data:image/png;base64,Ag==", imageUrls);
        Assert.DoesNotContain("data:image/png;base64,AA==", imageUrls);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(4, 3)]
    public void BuildModelMessages_RespectsConfiguredToolScreenshotCap(int maxScreenshots, int expectedCount)
    {
        var history = new List<ChatMessage>
        {
            ChatMessage.Create(MessageRole.User, "open browser")
        };
        AppendComputerObserveTurn(history, "call_1", "data:image/png;base64,AA==");
        AppendComputerObserveTurn(history, "call_2", "data:image/png;base64,AQ==");
        AppendComputerObserveTurn(history, "call_3", "data:image/png;base64,Ag==");

        var result = ModelMessagesForApiBuilder.Build(
            cache: null,
            "system",
            history,
            new ContextCompactionSettings { MaxToolScreenshotsInModelContext = maxScreenshots });
        var imageUrls = CollectImageUrls(result.Messages);

        Assert.Equal(expectedCount, imageUrls.Count);
        Assert.Contains("data:image/png;base64,Ag==", imageUrls);
    }

    [Fact]
    public void BuildModelMessages_UserUploadedImages_AreNotCappedByToolScreenshotLimit()
    {
        var history = new List<ChatMessage>
        {
            ChatMessage.Create(
                MessageRole.User,
                "use this reference",
                imageAttachments:
                [
                    new ImageAttachment("user.png", "image/png", DataUrl: "data:image/png;base64,USER")
                ])
        };
        AppendComputerObserveTurn(history, "call_1", "data:image/png;base64,AA==");
        AppendComputerObserveTurn(history, "call_2", "data:image/png;base64,AQ==");
        AppendComputerObserveTurn(history, "call_3", "data:image/png;base64,Ag==");

        var result = ModelMessagesForApiBuilder.Build(
            cache: null,
            "system",
            history,
            new ContextCompactionSettings { MaxToolScreenshotsInModelContext = 2 });
        var imageUrls = CollectImageUrls(result.Messages);

        Assert.Equal(3, imageUrls.Count);
        Assert.Contains("data:image/png;base64,USER", imageUrls);
        Assert.Contains("data:image/png;base64,AQ==", imageUrls);
        Assert.Contains("data:image/png;base64,Ag==", imageUrls);
        Assert.DoesNotContain("data:image/png;base64,AA==", imageUrls);
    }

    [Fact]
    public void ModelMessageCache_IncrementalBuild_ReappliesToolScreenshotCap()
    {
        var cache = new ModelMessageCache();
        var history = new List<ChatMessage>
        {
            ChatMessage.Create(MessageRole.User, "open browser")
        };
        AppendComputerObserveTurn(history, "call_1", "data:image/png;base64,AA==");
        ModelMessagesForApiBuilder.Build(
            cache,
            "system",
            history,
            new ContextCompactionSettings { MaxToolScreenshotsInModelContext = 2 });

        AppendComputerObserveTurn(history, "call_2", "data:image/png;base64,AQ==");
        AppendComputerObserveTurn(history, "call_3", "data:image/png;base64,Ag==");
        var result = ModelMessagesForApiBuilder.Build(
            cache,
            "system",
            history,
            new ContextCompactionSettings { MaxToolScreenshotsInModelContext = 2 });
        var imageUrls = CollectImageUrls(result.Messages);

        Assert.Equal(2, imageUrls.Count);
        Assert.Contains("data:image/png;base64,AQ==", imageUrls);
        Assert.Contains("data:image/png;base64,Ag==", imageUrls);
        Assert.DoesNotContain("data:image/png;base64,AA==", imageUrls);
    }

    private static void AppendComputerObserveTurn(
        List<ChatMessage> history,
        string toolCallId,
        string dataUrl)
    {
        var call = new AgentToolCall(toolCallId, "computer_observe", ToolCallArguments.Empty);
        history.Add(ChatMessage.Create(MessageRole.Assistant, string.Empty, toolCalls: [call]));
        history.Add(ChatMessage.Create(
            MessageRole.Tool,
            AgentRuntime.FormatToolResult(
                call,
                ToolResult.Success("observed", "{\"frame_id\":\"" + toolCallId + "\"}")),
            imageAttachments:
            [
                new ImageAttachment($"{toolCallId}.png", "image/png", DataUrl: dataUrl)
            ]));
    }

    private static List<string> CollectImageUrls(IReadOnlyList<AgentModelMessage> messages)
    {
        var urls = new List<string>();
        foreach (var message in messages)
        {
            if (message.Content is not IEnumerable<object> parts)
            {
                continue;
            }

            foreach (var part in parts)
            {
                if (part is not IDictionary<string, object?> map
                    || !map.TryGetValue("type", out var typeObj)
                    || typeObj is not string type
                    || !string.Equals(type, "image_url", StringComparison.Ordinal)
                    || !map.TryGetValue("image_url", out var imageObj)
                    || imageObj is not IDictionary<string, object?> imageMap
                    || !imageMap.TryGetValue("url", out var urlObj)
                    || urlObj is not string url)
                {
                    continue;
                }

                urls.Add(url);
            }
        }

        return urls;
    }
}
