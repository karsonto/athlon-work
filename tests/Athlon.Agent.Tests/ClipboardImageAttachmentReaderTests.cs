using System.Windows.Media;
using System.Windows.Media.Imaging;
using Athlon.Agent.App.Services;

namespace Athlon.Agent.Tests;

[Collection(TestCollections.Sta)]
[Trait("Category", TestCategories.UsesSta)]
public sealed class ClipboardImageAttachmentReaderTests
{
    [Fact]
    public void CreateFromBitmap_creates_png_data_url()
    {
        RunSta(() =>
        {
            var bitmap = CreateSolidColorBitmap(4, 4, Colors.Coral);
            var attachment = ClipboardImageAttachmentReader.CreateFromBitmap(bitmap, "clipboard-test.png");

            Assert.NotNull(attachment);
            Assert.Equal("clipboard-test.png", attachment!.FileName);
            Assert.Equal("image/png", attachment.MimeType);
            Assert.NotNull(attachment.DataUrl);
            Assert.StartsWith("data:image/png;base64,", attachment.DataUrl, StringComparison.OrdinalIgnoreCase);
            Assert.True(attachment.DataUrl.Length > "data:image/png;base64,".Length);
            Assert.Null(attachment.LocalPath);
        });
    }

    [Fact]
    public void CreateFromBitmap_uses_generated_name_when_missing()
    {
        RunSta(() =>
        {
            var bitmap = CreateSolidColorBitmap(2, 2, Colors.Blue);
            var attachment = ClipboardImageAttachmentReader.CreateFromBitmap(bitmap);

            Assert.NotNull(attachment);
            Assert.StartsWith("clipboard-", attachment!.FileName, StringComparison.Ordinal);
            Assert.EndsWith(".png", attachment.FileName, StringComparison.OrdinalIgnoreCase);
        });
    }

    private static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        })
        {
            IsBackground = true
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
        {
            throw failure;
        }
    }

    private static BitmapSource CreateSolidColorBitmap(int width, int height, Color color)
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
