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
        var uri = new Uri($"http://127.0.0.1:1{requestPath}");

        Assert.Null(AthlonWebStaticServer.ResolveRelativePath(uri));
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

        await using var server = new AthlonWebStaticServer(temp.Root);
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

        await using var server = new AthlonWebStaticServer(temp.Root);
        var baseUrl = await server.EnsureStartedAsync();

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var response = await http.GetAsync($"{baseUrl}../index.html");

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

        var server = new AthlonWebStaticServer(temp.Root);
        var baseUrl = await server.EnsureStartedAsync();
        await server.StopAsync();

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        await Assert.ThrowsAnyAsync<HttpRequestException>(() => http.GetAsync(baseUrl));
    }
}
