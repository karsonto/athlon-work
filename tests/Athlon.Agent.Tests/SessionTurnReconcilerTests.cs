using Athlon.Agent.Core;

namespace Athlon.Agent.Tests;

public sealed class SessionTurnReconcilerTests
{
    [Fact]
    public void Reconcile_CancelMidStream_PersistsPartialAssistantAndNotice()
    {
        var session = AgentSession.Create("test");
        var user = ChatMessage.Create(MessageRole.User, "hello");
        session = session.WithMessage(user);

        var snapshot = new SessionTurnEndSnapshot(
            "partial reply",
            "thinking",
            Array.Empty<AgentToolCall>(),
            WasCancelled: true,
            TimedOut: false,
            ErrorMessage: null);

        var result = SessionTurnReconciler.Reconcile(session, snapshot);

        Assert.Equal(3, result.Session.Messages.Count);
        Assert.Equal(2, result.PersistedMessages.Count);
        Assert.Equal(MessageRole.Assistant, result.Session.Messages[1].Role);
        Assert.Equal("partial reply", result.Session.Messages[1].Content);
        Assert.Equal("thinking", result.Session.Messages[1].ReasoningContent);
        Assert.Equal(MessageRole.System, result.Session.Messages[2].Role);
        Assert.Contains("生成已停止", result.Session.Messages[2].Content, StringComparison.Ordinal);
    }

    [Fact]
    public void Reconcile_MissingToolResult_PersistsFailureToolMessage()
    {
        var callId = "call_missing";
        var session = AgentSession.Create("test");
        session = session.WithMessage(ChatMessage.Create(MessageRole.User, "run"));
        session = session.WithMessage(ChatMessage.Create(
            MessageRole.Assistant,
            string.Empty,
            toolCalls: new[] { new AgentToolCall(callId, "file_read", new Dictionary<string, string> { ["path"] = "a.txt" }) }));

        var snapshot = new SessionTurnEndSnapshot(
            null,
            null,
            new[] { new AgentToolCall(callId, "file_read", new Dictionary<string, string> { ["path"] = "a.txt" }) },
            WasCancelled: true,
            TimedOut: false,
            ErrorMessage: null);

        var result = SessionTurnReconciler.Reconcile(session, snapshot);

        Assert.Equal(4, result.Session.Messages.Count);
        Assert.Equal(2, result.PersistedMessages.Count);
        Assert.Contains("用户停止", result.Session.Messages[2].Content, StringComparison.Ordinal);
        Assert.Contains("生成已停止", result.Session.Messages[3].Content, StringComparison.Ordinal);
    }

    [Fact]
    public void Reconcile_ModelError_PersistsSystemNoticeAndToolFailure()
    {
        var callId = "call_err";
        var session = AgentSession.Create("test");
        session = session.WithMessage(ChatMessage.Create(MessageRole.User, "run"));
        session = session.WithMessage(ChatMessage.Create(
            MessageRole.Assistant,
            string.Empty,
            toolCalls: new[] { new AgentToolCall(callId, "grep_files", new Dictionary<string, string>()) }));

        var snapshot = new SessionTurnEndSnapshot(
            null,
            null,
            new[] { new AgentToolCall(callId, "grep_files", new Dictionary<string, string>()) },
            WasCancelled: false,
            TimedOut: false,
            ErrorMessage: "模型调用失败：timeout");

        var result = SessionTurnReconciler.Reconcile(session, snapshot);

        Assert.Contains(result.Session.Messages, message => message.Role == MessageRole.System && message.Content.Contains("timeout", StringComparison.Ordinal));
        Assert.Contains(result.Session.Messages, message => message.Role == MessageRole.Tool && message.Content.Contains(callId, StringComparison.Ordinal));
    }

    [Fact]
    public void Reconcile_ModelError_DoesNotDuplicateFailurePrefix()
    {
        var session = AgentSession.Create("test");
        session = session.WithMessage(ChatMessage.Create(MessageRole.User, "continue"));

        var snapshot = new SessionTurnEndSnapshot(
            null,
            null,
            Array.Empty<AgentToolCall>(),
            WasCancelled: false,
            TimedOut: false,
            ErrorMessage: "模型调用失败：无法写入或读取「C:\\tmp\\session.json」：Access denied.");

        var result = SessionTurnReconciler.Reconcile(session, snapshot);
        var notice = Assert.Single(result.PersistedMessages, message => message.Role == MessageRole.System);

        Assert.Equal(snapshot.ErrorMessage, notice.Content);
        Assert.DoesNotContain("模型调用失败：模型调用失败", notice.Content, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildModelMessages_IncludesPersistedSystemNotice()
    {
        var session = AgentSession.Create("test");
        session = session.WithMessage(ChatMessage.Create(MessageRole.User, "continue"));
        session = session.WithMessage(ChatMessage.Create(MessageRole.System, "生成已停止。"));

        var messages = AgentRuntime.BuildModelMessages("system", session.Messages);

        Assert.Contains(messages, message => message.Role == "user" && message.Content.ToString()!.Contains("生成已停止", StringComparison.Ordinal));
    }
}
