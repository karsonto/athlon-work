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
    public void BeginEdit_CopiesDraftImages()
    {
        var images = new[]
        {
            new ImageAttachment("a.png", "image/png", "data:image/png;base64,AA=="),
            new ImageAttachment("b.png", "image/png", "data:image/png;base64,BB=="),
        };
        var vm = QueuedTurnViewModel.Create("q1", "caption", images);

        vm.BeginEdit();

        Assert.True(vm.IsEditing);
        Assert.Equal("caption", vm.DraftText);
        Assert.Equal(2, vm.DraftImages.Count);
        Assert.Equal("a.png", vm.DraftImages[0].FileName);
        Assert.Equal("b.png", vm.DraftImages[1].FileName);
    }

    [Fact]
    public void BeginEdit_And_CancelEdit_RestoreDraft()
    {
        var vm = QueuedTurnViewModel.Create("q1", "original", Array.Empty<ImageAttachment>());
        vm.BeginEdit();
        Assert.True(vm.IsEditing);
        Assert.Equal("original", vm.DraftText);
        vm.DraftText = "changed";
        vm.AddDraftImages([new ImageAttachment("x.png", "image/png", "data:image/png;base64,XX==")]);
        vm.CancelEdit();
        Assert.False(vm.IsEditing);
        Assert.Equal("original", vm.TextContent);
        Assert.Equal("original", vm.DraftText);
        Assert.Empty(vm.DraftImages);
        Assert.Equal(0, vm.ImageCount);
    }

    [Fact]
    public void ApplySaved_UpdatesPreviewImagesAndExitsEdit()
    {
        var vm = QueuedTurnViewModel.Create("q1", "old", Array.Empty<ImageAttachment>());
        var savedImages = new[]
        {
            new ImageAttachment("saved.png", "image/png", "data:image/png;base64,CC=="),
        };

        vm.BeginEdit();
        vm.ApplySaved("new text", savedImages);

        Assert.False(vm.IsEditing);
        Assert.Equal("new text", vm.TextContent);
        Assert.Equal("new text · 1 张图片", vm.PreviewText);
        Assert.Single(vm.Images);
        Assert.Equal("saved.png", vm.ImageItems[0].FileName);
        Assert.Empty(vm.DraftImages);
    }

    [Fact]
    public void AddDraftImages_DeduplicatesByDataUrl()
    {
        var image = new ImageAttachment("a.png", "image/png", "data:image/png;base64,AA==");
        var vm = QueuedTurnViewModel.Create("q1", "text", Array.Empty<ImageAttachment>());
        vm.BeginEdit();

        vm.AddDraftImages([image, image]);

        Assert.Single(vm.DraftImages);
    }

    [Fact]
    public void RemoveDraftImage_RemovesMatchingItem()
    {
        var images = new[]
        {
            new ImageAttachment("a.png", "image/png", "data:image/png;base64,AA=="),
            new ImageAttachment("b.png", "image/png", "data:image/png;base64,BB=="),
        };
        var vm = QueuedTurnViewModel.Create("q1", "text", images);
        vm.BeginEdit();
        var toRemove = vm.DraftImages[0];

        vm.RemoveDraftImage(toRemove);

        Assert.Single(vm.DraftImages);
        Assert.Equal("b.png", vm.DraftImages[0].FileName);
    }
}
