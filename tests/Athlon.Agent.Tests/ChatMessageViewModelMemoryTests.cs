using Athlon.Agent.App.ViewModels;

namespace Athlon.Agent.Tests;

public sealed class ChatMessageViewModelMemoryTests
{
    [Fact]
    public void TruncateToolDetailForDisplay_limits_collapsed_preview()
    {
        var detail = new string('x', 5000);
        var preview = ChatMessageViewModel.TruncateToolDetailForDisplay(detail, 4096);
        Assert.True(preview.Length < detail.Length);
        Assert.EndsWith("…", preview);
    }

    [Fact]
    public void StreamingBuilder_survives_flush_without_losing_incremental_content()
    {
        var viewModel = ChatMessageViewModel.CreateStreamingAssistant("stream-1");

        viewModel.AppendStreamingToken("hello");
        Assert.True(viewModel.HasBufferedStreamingContent());
        viewModel.FlushStreamingContent();
        Assert.Equal("hello", viewModel.Content);
        Assert.False(viewModel.HasBufferedStreamingContent());

        viewModel.AppendStreamingToken(" world");
        Assert.True(viewModel.HasBufferedStreamingContent());
        viewModel.FlushStreamingContent();
        Assert.Equal("hello world", viewModel.Content);
        Assert.False(viewModel.HasBufferedStreamingContent());
    }
}
