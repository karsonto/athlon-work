using System.Text.Json;
using Athlon.Agent.App.Services;
using Athlon.Agent.App.ViewModels;
using Athlon.Agent.Core;

namespace Athlon.Agent.Tests;

public sealed class UserMessageImageBubbleTests
{
    [Fact]
    public void SerializeUserMessage_includes_image_data_urls()
    {
        var message = new ChatMessageViewModel(ChatMessage.Create(
            MessageRole.User,
            "看这张图",
            imageAttachments:
            [
                new ImageAttachment(
                    "shot.png",
                    "image/png",
                    "data:image/png;base64,AA==")
            ]));

        var json = ChatEventSerializer.SerializeUserMessage(message);
        using var doc = JsonDocument.Parse(json);

        Assert.Equal("USER_MESSAGE", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal("看这张图", doc.RootElement.GetProperty("content").GetString());
        Assert.Equal(1, doc.RootElement.GetProperty("images").GetArrayLength());
        Assert.Equal(
            "data:image/png;base64,AA==",
            doc.RootElement.GetProperty("images")[0].GetProperty("url").GetString());
        Assert.DoesNotContain("image(s) attached", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SerializeUserMessage_falls_back_to_summary_without_resolvable_images()
    {
        var message = new ChatMessageViewModel(ChatMessage.Create(
            MessageRole.User,
            "hi",
            imageAttachments:
            [
                new ImageAttachment("missing.png", "image/png", DataUrl: null, LocalPath: @"C:\no-such\file.png")
            ]));

        var json = ChatEventSerializer.SerializeUserMessage(message);
        using var doc = JsonDocument.Parse(json);

        Assert.Equal(0, doc.RootElement.GetProperty("images").GetArrayLength());
        Assert.Contains(message.UserAttachmentSummary, doc.RootElement.GetProperty("content").GetString());
    }
}
