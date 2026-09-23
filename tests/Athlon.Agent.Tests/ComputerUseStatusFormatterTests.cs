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

    // ---- Action log (Phase 5.2) ------------------------------------------

    [Fact]
    public void FormatActionLine_ComposesNameActionResolutionAndStatus()
    {
        var line = ComputerUseStatusFormatter.FormatActionLine(
            "computer_interact",
            "action = click\nimage_x = 120\nimage_y = 88",
            "{\n  \"resolved_via\": \"image_point\"\n}",
            "已完成");

        Assert.Equal("computer_interact · click · image_point · 已完成", line);
    }

    [Fact]
    public void FormatActionLine_OmitsActionAndResolutionWhenAbsent()
    {
        var line = ComputerUseStatusFormatter.FormatActionLine(
            "computer_observe",
            "(none)",
            "{\"ui_tree\":[]}",
            "运行中");

        Assert.Equal("computer_observe · 运行中", line);
    }

    [Fact]
    public void FormatActionLine_ReturnsEmptyForNonTool()
    {
        Assert.Equal(string.Empty, ComputerUseStatusFormatter.FormatActionLine(null, "action = click", null, null));
        Assert.Equal(string.Empty, ComputerUseStatusFormatter.FormatActionLine("  ", null, null, null));
    }

    [Fact]
    public void FormatActionLine_TruncatesToMaxLength()
    {
        var line = ComputerUseStatusFormatter.FormatActionLine(
            "computer_interact",
            "action = " + new string('x', 400),
            null,
            null);

        Assert.Equal(ComputerUseStatusFormatter.ActionLineMaxLength, line.Length);
        Assert.EndsWith("…", line);
    }

    [Fact]
    public void ExtractActionName_IgnoresUnrelatedArguments()
    {
        Assert.Equal("type_text", ComputerUseStatusFormatter.ExtractActionName("text = hello\naction = type_text"));
        Assert.Equal(string.Empty, ComputerUseStatusFormatter.ExtractActionName("text = hello"));
        Assert.Equal(string.Empty, ComputerUseStatusFormatter.ExtractActionName(null));
    }

    [Fact]
    public void ExtractResolvedVia_ReadsEnvelopeField()
    {
        Assert.Equal(
            "element_native",
            ComputerUseStatusFormatter.ExtractResolvedVia("{\n  \"resolved_via\": \"element_native\",\n  \"ui_tree\": []\n}"));
        Assert.Equal(string.Empty, ComputerUseStatusFormatter.ExtractResolvedVia("{\"frame_id\":\"f\"}"));
        Assert.Equal(string.Empty, ComputerUseStatusFormatter.ExtractResolvedVia(null));
    }

    [Fact]
    public void ComputerUseActionLine_IsOnlyVisibleForComputerTools()
    {
        var computer = ChatMessageViewModel.CreatePendingTool(
            new AgentToolCall("t1", "computer_observe", ToolCallArguments.Empty));
        var other = ChatMessageViewModel.CreatePendingTool(
            new AgentToolCall("t2", "file_read", ToolCallArguments.Empty));

        Assert.True(computer.IsComputerUseActionLineVisible);
        Assert.StartsWith("computer_observe", computer.ComputerUseActionLine);
        Assert.False(other.IsComputerUseActionLineVisible);
        Assert.Equal(string.Empty, other.ComputerUseActionLine);
    }
}
