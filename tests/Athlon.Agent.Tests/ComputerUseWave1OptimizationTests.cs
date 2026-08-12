using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Athlon.Agent.App.Services.ComputerUse;
using Athlon.Agent.Core.ComputerUse;
using Athlon.Agent.Infrastructure.ComputerUse;

namespace Athlon.Agent.Tests;

public sealed class ComputerUseWave1OptimizationTests
{
    [Fact]
    public void ImageToPhysical_MapsWithScaleAndMonitorOrigin()
    {
        var (x, y) = ComputerUseCoordinateMapper.ImageToPhysical(
            imageX: 100,
            imageY: 50,
            monitorLeft: 1920,
            monitorTop: 0,
            captureWidth: 3840,
            captureHeight: 2160,
            imageWidth: 1600,
            imageHeight: 900);

        Assert.Equal(1920 + 240, x);
        Assert.Equal(120, y);
    }

    [Fact]
    public void ImageToPhysical_IdentityWhenImageMatchesCapture()
    {
        var (x, y) = ComputerUseCoordinateMapper.ImageToPhysical(
            imageX: 10,
            imageY: 20,
            monitorLeft: 100,
            monitorTop: 200,
            captureWidth: 800,
            captureHeight: 600,
            imageWidth: 800,
            imageHeight: 600);

        Assert.Equal(110, x);
        Assert.Equal(220, y);
    }

    [Fact]
    public void FitWithin_DownscalesLongestEdge()
    {
        var (width, height) = ComputerUseScreenshotSizing.FitWithin(3840, 2160);
        Assert.Equal(1600, width);
        Assert.Equal(900, height);
    }

    [Fact]
    public void FitWithin_LeavesSmallImagesUnchanged()
    {
        var (width, height) = ComputerUseScreenshotSizing.FitWithin(1280, 720);
        Assert.Equal(1280, width);
        Assert.Equal(720, height);
    }

    [Fact]
    public void UiNodeFilter_SkipsOffscreenAndEmptyBounds()
    {
        Assert.False(ComputerUseUiNodeFilter.ShouldInclude(
            isRoot: false,
            isOffscreen: true,
            boundsWidth: 40,
            boundsHeight: 20));
        Assert.False(ComputerUseUiNodeFilter.ShouldInclude(
            isRoot: false,
            isOffscreen: false,
            boundsWidth: 0,
            boundsHeight: 10));
        Assert.True(ComputerUseUiNodeFilter.ShouldInclude(
            isRoot: true,
            isOffscreen: true,
            boundsWidth: 0,
            boundsHeight: 0));
        Assert.True(ComputerUseUiNodeFilter.ShouldInclude(
            isRoot: false,
            isOffscreen: false,
            boundsWidth: 40,
            boundsHeight: 20));
    }

    [Fact]
    public void UiNodeFilter_SkipsNodesOutsideMonitor()
    {
        Assert.False(ComputerUseUiNodeFilter.ShouldInclude(
            isRoot: false,
            isOffscreen: false,
            boundsWidth: 50,
            boundsHeight: 50,
            monitorLeft: 0,
            monitorTop: 0,
            monitorWidth: 100,
            monitorHeight: 100,
            boundsLeft: 200,
            boundsTop: 200));
        Assert.True(ComputerUseUiNodeFilter.ShouldInclude(
            isRoot: false,
            isOffscreen: false,
            boundsWidth: 50,
            boundsHeight: 50,
            monitorLeft: 0,
            monitorTop: 0,
            monitorWidth: 100,
            monitorHeight: 100,
            boundsLeft: 80,
            boundsTop: 80));
    }

    [Fact]
    public void FormatError_SerializesComputerUseExceptionCode()
    {
        var json = ComputerUseToolHelper.FormatError(
            new ComputerUseException("stale_frame", "Frame expired."));
        using var document = JsonDocument.Parse(json);
        Assert.Equal("stale_frame", document.RootElement.GetProperty("code").GetString());
        Assert.Equal("Frame expired.", document.RootElement.GetProperty("message").GetString());
        Assert.Equal("call computer_observe", document.RootElement.GetProperty("hint").GetString());
    }

    [Fact]
    public void FormatError_MapsTimeoutToUiaTimeout()
    {
        var json = ComputerUseToolHelper.FormatError(new TimeoutException("slow"));
        using var document = JsonDocument.Parse(json);
        Assert.Equal("uia_timeout", document.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public void ScreenshotEncoder_ProducesJpegWithinMaxEdge()
    {
        var source = CreateSolidBitmap(2400, 1200, Colors.SteelBlue);
        var encoded = ComputerUseScreenshotEncoder.Encode(source, 2400, 1200);

        Assert.Equal("image/jpeg", encoded.MimeType);
        Assert.Equal(2400, encoded.CaptureWidth);
        Assert.Equal(1200, encoded.CaptureHeight);
        Assert.Equal(1600, encoded.ImageWidth);
        Assert.Equal(800, encoded.ImageHeight);
        Assert.True(encoded.Bytes.Length > 100);
        Assert.Equal(0xFF, encoded.Bytes[0]);
        Assert.Equal(0xD8, encoded.Bytes[1]);
    }

    [Fact]
    public void ObserveRequest_DefaultsAreSlim()
    {
        var request = new ComputerUseObserveRequest();
        Assert.Equal(4, request.MaxTreeDepth);
        Assert.Equal(80, request.MaxNodes);
        Assert.True(request.IncludeUiTree);
    }

    [Fact]
    public void DragPath_InterpolatesAndEndsExactly()
    {
        var path = ComputerUseDragPath.Build(0, 0, 100, 50, steps: 4);
        Assert.Equal(4, path.Count);
        Assert.Equal((25, 12), path[0]);
        Assert.Equal((50, 25), path[1]);
        Assert.Equal((75, 38), path[2]);
        Assert.Equal((100, 50), path[^1]);
    }

    [Fact]
    public void DragPath_SamePointReturnsSingleEnd()
    {
        var path = ComputerUseDragPath.Build(10, 20, 10, 20);
        Assert.Equal([(10, 20)], path);
    }

    private static BitmapSource CreateSolidBitmap(int width, int height, Color color)
    {
        var pixels = new byte[width * height * 4];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = color.B;
            pixels[i + 1] = color.G;
            pixels[i + 2] = color.R;
            pixels[i + 3] = color.A;
        }

        var bitmap = BitmapSource.Create(
            width,
            height,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            pixels,
            width * 4);
        bitmap.Freeze();
        return bitmap;
    }
}
