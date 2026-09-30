using Athlon.Agent.Core;

namespace Athlon.Agent.Tests;

public sealed class SessionForkTests
{
    [Fact]
    public void TrySlice_KeepsToolPairAndCompaction_AndDropsTheForkPointOnward()
    {
        var compaction = ChatMessage.CreateWithId("c1", MessageRole.Compaction, "earlier context");
        var firstUser = ChatMessage.CreateWithId("u1", MessageRole.User, "first");
        var assistant = ChatMessage.CreateWithId(
            "a1",
            MessageRole.Assistant,
            "calling",
            toolCalls: [new AgentToolCall("call-1", "execute_command", new Dictionary<string, string>())]);
        var tool = ChatMessage.CreateWithId(
            ChatMessage.ToolResultMessageId("call-1"),
            MessageRole.Tool,
            "ToolCallId: call-1\nok");
        var forkUser = ChatMessage.CreateWithId("u2", MessageRole.User, "try another way");
        var later = ChatMessage.CreateWithId("a2", MessageRole.Assistant, "later");

        var slice = SessionFork.TrySlice([compaction, firstUser, assistant, tool, forkUser, later], "u2");

        Assert.NotNull(slice);
        Assert.Equal(["c1", "u1", "a1", "tool:call-1"], slice.Prefix.Select(message => message.Id).ToArray());
        Assert.Equal(MessageRole.Compaction, slice.Prefix[0].Role);
        Assert.Equal("u2", slice.ComposerMessage.Id);
        Assert.Equal("try another way", slice.ComposerMessage.Content);
        Assert.DoesNotContain(slice.Prefix, message => message.Id is "u2" or "a2");
    }

    [Fact]
    public void TrySlice_ReturnsNull_ForAssistantOrMissingMessages()
    {
        var user = ChatMessage.CreateWithId("u1", MessageRole.User, "hello");
        var assistant = ChatMessage.CreateWithId("a1", MessageRole.Assistant, "hi");

        Assert.Null(SessionFork.TrySlice([user, assistant], "a1"));
        Assert.Null(SessionFork.TrySlice([user, assistant], "missing"));
        Assert.Null(SessionFork.TrySlice([user], " "));
    }

    [Fact]
    public void CreateSession_CopiesWorkspaceAndLeavesOutTheComposerMessage()
    {
        var source = new AgentSession(
            "source",
            "修复滚动",
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            "/work",
            "skill",
            "model",
            [ChatMessage.CreateWithId("u1", MessageRole.User, "hello")])
        {
            ActiveWorkspaceId = "ws-1"
        };
        var prefix = new[] { ChatMessage.CreateWithId("c1", MessageRole.Compaction, "summary") };

        var forked = SessionFork.CreateSession(source, prefix);

        Assert.NotEqual(source.Id, forked.Id);
        Assert.Equal("修复滚动 的分叉", forked.Title);
        Assert.Equal("/work", forked.ActiveWorkspace);
        Assert.Equal("ws-1", forked.ActiveWorkspaceId);
        Assert.Equal("skill", forked.ActiveSkill);
        Assert.Equal("model", forked.ModelName);
        Assert.Equal(["c1"], forked.Messages.Select(message => message.Id).ToArray());
    }

    [Fact]
    public void ToolCallIds_ReadsAssistantCallsAndToolResults()
    {
        var assistant = ChatMessage.CreateWithId(
            "a1",
            MessageRole.Assistant,
            "",
            toolCalls: [new AgentToolCall("call-1", "file_read", new Dictionary<string, string>())]);
        var tool = ChatMessage.CreateWithId("tool:call-1", MessageRole.Tool, "ToolCallId: call-1\nbody");

        var ids = SessionFork.ToolCallIds([assistant, tool]);

        Assert.Equal(["call-1"], ids);
    }
}
