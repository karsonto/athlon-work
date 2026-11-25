using System.Net;
using System.Runtime.Versioning;
using System.Text;
using Athlon.Agent.App.Services;

namespace Athlon.Agent.Tests;

public sealed class AthlonWebStaticServerTests
{
    [Theory]
    [InlineData("/", "index.html")]
    [InlineData("", "index.html")]
    [InlineData("/index.html", "index.html")]
    public void ResolveRelativePath_MapsRequestPaths(string requestPath, string expected)
    {
        var uri = string.IsNullOrEmpty(requestPath)
            ? null
            : new Uri($"http://127.0.0.1:1{requestPath}");

        var resolved = AthlonWebStaticServer.ResolveRelativePath(uri);

        Assert.Equal(expected, resolved);
    }

    [Fact]
    public void ResolveRelativePath_MapsNestedPaths()
    {
        var uri = new Uri("http://127.0.0.1:1/css/app.css");

        var resolved = AthlonWebStaticServer.ResolveRelativePath(uri);

        Assert.Equal(Path.Combine("css", "app.css"), resolved);
    }

    [Theory]
    [InlineData("/../index.html")]
    [InlineData("/css/../../secret.txt")]
    [InlineData("/./../index.html")]
    public void ResolveRelativePath_RejectsTraversal(string requestPath)
    {
        // AbsolutePath collapses ".." — ResolveRelativePath reads OriginalString / ResolveRawPath.
        var uri = new Uri($"http://127.0.0.1:1{requestPath}");

        Assert.Null(AthlonWebStaticServer.ResolveRelativePath(uri));
        Assert.Null(AthlonWebStaticServer.ResolveRawPath(requestPath));
    }

    [Fact]
    public void IsPathWithinRoot_RejectsPathsOutsideRoot()
    {
        using var temp = new TempDirectoryScope("athlon-web");
        var root = Path.Combine(temp.Root, "web");
        Directory.CreateDirectory(root);

        var inside = Path.Combine(root, "index.html");
        var outside = Path.Combine(temp.Root, "outside.txt");

        Assert.True(AthlonWebStaticServer.IsPathWithinRoot(root, inside));
        Assert.False(AthlonWebStaticServer.IsPathWithinRoot(root, outside));
    }

    [SupportedOSPlatform("windows")]
    [Trait("Category", TestCategories.Integration)]
    [Trait("Category", TestCategories.UsesHttp)]
    [Fact]
    public async Task EnsureStartedAsync_ServesIndexHtml_OnLoopback()
    {
        using var temp = new TempDirectoryScope("athlon-web");
        var html = "<html><body>Athlon Web test</body></html>";
        await File.WriteAllTextAsync(Path.Combine(temp.Root, "index.html"), html, Encoding.UTF8);

        await using var server = new AthlonWebStaticServer(temp.Root, port: 0);
        var baseUrl = await server.EnsureStartedAsync();

        Assert.False(string.IsNullOrWhiteSpace(baseUrl));
        Assert.StartsWith("http://", baseUrl, StringComparison.OrdinalIgnoreCase);

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var response = await http.GetAsync(baseUrl);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Athlon Web test", body, StringComparison.Ordinal);
    }

    [SupportedOSPlatform("windows")]
    [Trait("Category", TestCategories.Integration)]
    [Trait("Category", TestCategories.UsesHttp)]
    [Fact]
    public async Task EnsureStartedAsync_RejectsPathTraversal()
    {
        using var temp = new TempDirectoryScope("athlon-web");
        await File.WriteAllTextAsync(Path.Combine(temp.Root, "index.html"), "ok", Encoding.UTF8);

        await using var server = new AthlonWebStaticServer(temp.Root, port: 0);
        var baseUrl = await server.EnsureStartedAsync();

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        // HttpClient otherwise collapses ".." before the request is sent, so the server never
        // sees the traversal. Keep the original target so AbsolutePath/RawUrl can still differ.
        var options = new UriCreationOptions { DangerousDisablePathAndQueryCanonicalization = true };
        var traversal = new Uri($"{baseUrl}../index.html", in options);
        var response = await http.GetAsync(traversal);

        Assert.True(response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound);
    }

    [SupportedOSPlatform("windows")]
    [Trait("Category", TestCategories.Integration)]
    [Trait("Category", TestCategories.UsesHttp)]
    [Fact]
    public async Task StopAsync_PreventsFurtherRequests()
    {
        using var temp = new TempDirectoryScope("athlon-web");
        await File.WriteAllTextAsync(Path.Combine(temp.Root, "index.html"), "ok", Encoding.UTF8);

        var server = new AthlonWebStaticServer(temp.Root, port: 0);
        var baseUrl = await server.EnsureStartedAsync();
        await server.StopAsync();

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        // After Abort/Stop, runners may surface connection refused (HttpRequestException)
        // or a client-side timeout (TaskCanceledException).
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => http.GetAsync(baseUrl));
        Assert.True(
            ex is HttpRequestException or TaskCanceledException or TimeoutException,
            $"Unexpected exception type: {ex.GetType().FullName}");
    }
}
