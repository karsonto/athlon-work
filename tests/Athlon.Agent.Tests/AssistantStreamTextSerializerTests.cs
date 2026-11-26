using System.Text.Json;
using Athlon.Agent.App.Services;
using Athlon.Agent.Core;

namespace Athlon.Agent.Tests;

public sealed class AssistantStreamTextSerializerTests
{
    [Fact]
    public void SerializeAssistantStreamText_emits_plain_text_event_without_html()
    {
        var json = ChatEventSerializer.SerializeAssistantStreamText("msg-1", "line1\nline2");

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.Equal("ASSISTANT_STREAM_TEXT", root.GetProperty("type").GetString());
        Assert.Equal("msg-1", root.GetProperty("messageId").GetString());
        Assert.Equal("line1\nline2", root.GetProperty("text").GetString());
        Assert.True(root.GetProperty("createIfMissing").GetBoolean());
        Assert.False(root.TryGetProperty("html", out _));
    }

    [Fact]
    public void ChatRenderTurnEndReplay_defaults_to_false()
    {
        var ui = new UiSettings();
        Assert.False(ui.ChatRenderTurnEndReplay);
        Assert.True(ui.ChatRenderAtomicReplay);
    }
}
