using Athlon.Agent.App.Services;

namespace Athlon.Agent.Tests;

public sealed class WebView2RuntimeLocatorTests
{
    [Fact]
    public void TryResolveBundledFolder_FindsExeInExpectedLayout()
    {
        var root = Path.Combine(Path.GetTempPath(), "athlon-webview2-" + Guid.NewGuid().ToString("N"));
        var runtimeDir = Path.Combine(root, "runtimes", "webview2", "x64");
        Directory.CreateDirectory(runtimeDir);
        File.WriteAllText(Path.Combine(runtimeDir, "msedgewebview2.exe"), string.Empty);

        try
        {
            var resolved = WebView2RuntimeLocator.TryResolveBundledFolder(root);
            Assert.Equal(runtimeDir, resolved);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void TryResolveBundledFolder_FindsExeInNestedVersionFolder()
    {
        var root = Path.Combine(Path.GetTempPath(), "athlon-webview2-" + Guid.NewGuid().ToString("N"));
        var nested = Path.Combine(root, "runtimes", "webview2", "Microsoft.WebView2.FixedVersionRuntime.149.0.4022.80.x64");
        Directory.CreateDirectory(nested);
        File.WriteAllText(Path.Combine(nested, "msedgewebview2.exe"), string.Empty);

        try
        {
            var resolved = WebView2RuntimeLocator.TryResolveBundledFolder(root);
            Assert.Equal(nested, resolved);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void TryResolveBundledFolder_IgnoresUnrelatedFoldersOutsideRuntimeRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "athlon-webview2-" + Guid.NewGuid().ToString("N"));
        var unrelated = Path.Combine(root, "other", "Microsoft.WebView2.FixedVersionRuntime.149.0.4022.80.x64");
        Directory.CreateDirectory(unrelated);
        File.WriteAllText(Path.Combine(unrelated, "msedgewebview2.exe"), string.Empty);

        try
        {
            var resolved = WebView2RuntimeLocator.TryResolveBundledFolder(root);
            Assert.Null(resolved);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
