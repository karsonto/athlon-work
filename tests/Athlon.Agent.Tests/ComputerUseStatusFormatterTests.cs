using Athlon.Agent.App.Services.ComputerUse;
using Athlon.Agent.App.ViewModels;
using Athlon.Agent.Core;

namespace Athlon.Agent.Tests;

public sealed class ComputerUseStatusFormatterTests
{
    [Fact]
    public void FormatToolLine_UsesThinkingPlaceholder_WhenNoTool()
    {
        var text = ComputerUseStatusFormatter.FormatToolLine(
            null,
            null,
            "Thinking…",
            "{0} · {1}");
        Assert.Equal("Thinking…", text);
    }

    [Fact]
    public void FormatToolLine_FormatsNameAndStatus()
    {
        var text = ComputerUseStatusFormatter.FormatToolLine(
            "computer_interact",
            "Running",
            "Thinking…",
            "{0} · {1}");
        Assert.Equal("computer_interact · Running", text);
    }

    [Fact]
    public void FormatAssistantSummary_CollapsesWhitespaceAndTruncates()
    {
        var summary = ComputerUseStatusFormatter.FormatAssistantSummary(
            "第一行\n\n第二行   继续说明更多内容",
            maxLength: 10);
        Assert.Equal("第一行 第二行 继…", summary);
        Assert.Equal(10, summary.Length);
    }

    [Fact]
    public void FindLatestComputerUseTool_PrefersComputerPrefix()
    {
        var other = ChatMessageViewModel.CreatePendingTool(
            new AgentToolCall("t1", "file_read", ToolCallArguments.Empty));
        var computer = ChatMessageViewModel.CreatePendingTool(
            new AgentToolCall("t2", "computer_observe", ToolCallArguments.Empty));
        var messages = new List<ChatMessageViewModel>
        {
            other,
            computer,
            ChatMessageViewModel.CreatePendingTool(
                new AgentToolCall("t3", "grep_files", ToolCallArguments.Empty))
        };

        var found = ComputerUseStatusFormatter.FindLatestComputerUseTool(messages);
        Assert.Same(computer, found);
    }

    [Fact]
    public void FindLatestComputerUseTool_FallsBackToAnyTool()
    {
        var tool = ChatMessageViewModel.CreatePendingTool(
            new AgentToolCall("t1", "file_read", ToolCallArguments.Empty));
        var found = ComputerUseStatusFormatter.FindLatestComputerUseTool([tool]);
        Assert.Same(tool, found);
    }

    [Fact]
    public void FindLatestAssistantWithContent_SkipsEmptyAndTools()
    {
        var emptyAssistant = ChatMessageViewModel.CreateStreamingAssistant();
        var tool = ChatMessageViewModel.CreatePendingTool(
            new AgentToolCall("t1", "computer_interact", ToolCallArguments.Empty));
        var assistant = new ChatMessageViewModel(
            ChatMessage.Create(MessageRole.Assistant, "打开浏览器"));
        var messages = new List<ChatMessageViewModel>
        {
            emptyAssistant,
            tool,
            assistant
        };

        var found = ComputerUseStatusFormatter.FindLatestAssistantWithContent(messages);
        Assert.Same(assistant, found);
        Assert.Equal(
            "打开浏览器",
            ComputerUseStatusFormatter.FormatAssistantSummary(found!.Content));
    }
}
