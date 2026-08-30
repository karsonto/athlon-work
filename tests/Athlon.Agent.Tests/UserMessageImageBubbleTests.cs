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
        Assert.Equal(
            ChatEventSerializer.FormatStartedAt(message.CreatedAtUtc),
            doc.RootElement.GetProperty("startedAt").GetString());
        Assert.DoesNotContain("image(s) attached", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SerializeUserMessage_includes_file_mentions_without_at_prefix()
    {
        var message = new ChatMessageViewModel(ChatMessage.Create(
            MessageRole.User,
            "分析 @index.html"));

        var json = ChatEventSerializer.SerializeUserMessage(message);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal("分析 @index.html", root.GetProperty("content").GetString());
        var mention = Assert.Single(root.GetProperty("mentions").EnumerateArray());
        Assert.Equal(3, mention.GetProperty("start").GetInt32());
        Assert.Equal("index.html", mention.GetProperty("fileName").GetString());
        Assert.Equal("index.html", mention.GetProperty("path").GetString());
        Assert.Equal("file", mention.GetProperty("kind").GetString());
        Assert.Equal("Html", mention.GetProperty("iconKind").GetString());
        Assert.DoesNotContain("@", mention.GetProperty("fileName").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public void SerializeUserMessage_includes_skill_and_mcp_mentions_without_prefix()
    {
        var message = new ChatMessageViewModel(ChatMessage.Create(
            MessageRole.User,
            "use //skill:demo and //mcp:demo-server"));

        var json = ChatEventSerializer.SerializeUserMessage(message);
        using var doc = JsonDocument.Parse(json);
        var mentions = doc.RootElement.GetProperty("mentions").EnumerateArray().ToArray();

        Assert.Equal(2, mentions.Length);
        Assert.Equal("skill", mentions[0].GetProperty("kind").GetString());
        Assert.Equal("demo", mentions[0].GetProperty("fileName").GetString());
        Assert.DoesNotContain("//", mentions[0].GetProperty("fileName").GetString(), StringComparison.Ordinal);
        Assert.False(mentions[0].TryGetProperty("iconKind", out _));
        Assert.Equal("mcp", mentions[1].GetProperty("kind").GetString());
        Assert.Equal("demo-server", mentions[1].GetProperty("fileName").GetString());
    }

    [Fact]
    public void SerializeUserMessage_strips_skill_expansion_preamble_from_timeline()
    {
        var expanded = SkillComposerExpander.Expand(
            "//skill:industrial-brutalist-ui nishi",
            [new AvailableSkillInfo("industrial-brutalist-ui", "Brutalist", "industrial-brutalist-ui")]);
        var message = new ChatMessageViewModel(ChatMessage.Create(MessageRole.User, expanded));

        var json = ChatEventSerializer.SerializeUserMessage(message);
        using var doc = JsonDocument.Parse(json);
        var content = doc.RootElement.GetProperty("content").GetString();

        Assert.Equal("//skill:industrial-brutalist-ui nishi", content);
        Assert.DoesNotContain("Skill reference", content, StringComparison.Ordinal);
        Assert.DoesNotContain("load_skill_through_path", content, StringComparison.Ordinal);
        Assert.DoesNotContain("SKILL.md", content, StringComparison.Ordinal);
        var mention = Assert.Single(doc.RootElement.GetProperty("mentions").EnumerateArray());
        Assert.Equal("skill", mention.GetProperty("kind").GetString());
        Assert.Equal("industrial-brutalist-ui", mention.GetProperty("fileName").GetString());
    }

    [Fact]
    public void SerializeUserMessage_omits_mentions_for_plain_text()
    {
        var json = ChatEventSerializer.SerializeUserMessage(
            new ChatMessageViewModel(ChatMessage.Create(MessageRole.User, "hello")));
        using var doc = JsonDocument.Parse(json);

        Assert.False(doc.RootElement.TryGetProperty("mentions", out _));
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
