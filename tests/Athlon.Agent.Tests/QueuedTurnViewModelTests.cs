using Athlon.Agent.App.ViewModels;
using Athlon.Agent.Core;

namespace Athlon.Agent.Tests;

public sealed class QueuedTurnViewModelTests
{
    [Fact]
    public void BuildPreview_TextOnly_ReturnsTrimmedText()
    {
        var preview = QueuedTurnViewModel.BuildPreview("  hello  ", 0);
        Assert.Equal("hello", preview);
    }

    [Fact]
    public void BuildPreview_ImagesOnly_ShowsImageCount()
    {
        var preview = QueuedTurnViewModel.BuildPreview("   ", 2);
        Assert.Equal("（2 张图片）", preview);
    }

    [Fact]
    public void BuildPreview_TextAndImages_AppendsImageSuffix()
    {
        var preview = QueuedTurnViewModel.BuildPreview("分析截图", 1);
        Assert.Equal("分析截图 · 1 张图片", preview);
    }

    [Fact]
    public void Create_KeepsImageAttachments()
    {
        var images = new[]
        {
            new ImageAttachment("a.png", "image/png", "data:image/png;base64,AA=="),
        };

        var vm = QueuedTurnViewModel.Create("q1", "说明", images);

        Assert.Single(vm.Images);
        Assert.Equal("a.png", vm.ImageItems[0].FileName);
        Assert.True(vm.HasText);
        Assert.True(vm.HasImages);
    }

    [Fact]
    public void Create_PreservesLeadingTrailingWhitespaceAndNewlines()
    {
        const string input = "\n```csharp\ncode\n```\n\n";

        var vm = QueuedTurnViewModel.Create("q1", input, Array.Empty<ImageAttachment>());

        Assert.Equal(input, vm.TextContent);
    }

    [Fact]
    public void BeginEdit_And_CancelEdit_RestoreDraft()
    {
        var vm = QueuedTurnViewModel.Create("q1", "original", Array.Empty<ImageAttachment>());
        vm.BeginEdit();
        Assert.True(vm.IsEditing);
        Assert.Equal("original", vm.DraftText);
        vm.DraftText = "changed";
        vm.CancelEdit();
        Assert.False(vm.IsEditing);
        Assert.Equal("original", vm.TextContent);
        Assert.Equal("original", vm.DraftText);
    }

    [Fact]
    public void ApplySavedText_UpdatesPreviewAndExitsEdit()
    {
        var vm = QueuedTurnViewModel.Create("q1", "old", Array.Empty<ImageAttachment>());
        vm.BeginEdit();
        vm.ApplySavedText("new text");
        Assert.False(vm.IsEditing);
        Assert.Equal("new text", vm.TextContent);
        Assert.Equal("new text", vm.PreviewText);
    }
}
