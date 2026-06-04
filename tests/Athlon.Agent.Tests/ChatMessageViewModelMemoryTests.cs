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
}
