using System.Text.RegularExpressions;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Browser;

namespace Athlon.Agent.App.Services;

/// <summary>
/// Rewrites report citations of the form <c>screenshot:browser-frame-….png</c> into the
/// attachment's data URL. Any other address is left unchanged.
/// </summary>
internal static partial class BrowserScreenshotMarkdown
{
    [GeneratedRegex(@"\(screenshot:([^)\s]+)\)", RegexOptions.CultureInvariant)]
    private static partial Regex ScreenshotReference();

    public static void Collect(IReadOnlyList<ImageAttachment>? images, List<ImageAttachment> destination)
    {
        if (images is not { Count: > 0 })
        {
            return;
        }

        foreach (var image in images)
        {
            if (!image.FileName.StartsWith(BrowserFrameFiles.Prefix, StringComparison.Ordinal))
            {
                continue;
            }

            if (destination.Any(existing =>
                    string.Equals(existing.FileName, image.FileName, StringComparison.Ordinal)))
            {
                continue;
            }

            destination.Add(image);
        }
    }

    public static string Rewrite(string? markdown, IReadOnlyList<ImageAttachment>? images)
    {
        if (string.IsNullOrEmpty(markdown) || images is not { Count: > 0 })
        {
            return markdown ?? string.Empty;
        }

        return ScreenshotReference().Replace(markdown, match =>
        {
            var name = match.Groups[1].Value;
            if (!name.StartsWith(BrowserFrameFiles.Prefix, StringComparison.Ordinal))
            {
                return match.Value;
            }

            var image = images.FirstOrDefault(item =>
                string.Equals(item.FileName, name, StringComparison.Ordinal));
            if (image is null)
            {
                return match.Value;
            }

            var url = ImageAttachmentDataUrlResolver.ResolveDataUrl(image);
            return string.IsNullOrWhiteSpace(url) ? match.Value : "(" + url + ")";
        });
    }
}
