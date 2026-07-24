using Athlon.Agent.Mcp;
using ModelContextProtocol.Client;

namespace Athlon.Agent.Tests;

public sealed class McpTransportKindsTests
{
    [Theory]
    [InlineData("sse", true)]
    [InlineData("http", true)]
    [InlineData("https", true)]
    [InlineData("streamable-http", true)]
    [InlineData("stdio", false)]
    [InlineData(null, false)]
    [InlineData("", false)]
    public void IsHttp_RecognizesRemoteTransports(string? type, bool expected) =>
        Assert.Equal(expected, McpTransportKinds.IsHttp(type));

    [Theory]
    [InlineData("sse", "http://localhost:3100/mcp", HttpTransportMode.Sse)]
    [InlineData("SSE", "http://localhost:3100/anything", HttpTransportMode.Sse)]
    [InlineData("http", "http://localhost:3100/sse", HttpTransportMode.Sse)]
    [InlineData("http", "http://localhost:3100/sse?token=1", HttpTransportMode.Sse)]
    [InlineData("https", "https://example.com/v1/SSE/", HttpTransportMode.Sse)]
    [InlineData("streamable-http", "http://localhost:3100/mcp", HttpTransportMode.StreamableHttp)]
    [InlineData("streamable_http", "http://localhost:3100/mcp", HttpTransportMode.StreamableHttp)]
    [InlineData("http", "http://localhost:3100/mcp", HttpTransportMode.AutoDetect)]
    [InlineData("https", "https://example.com/mcp", HttpTransportMode.AutoDetect)]
    public void ResolveHttpTransportMode_PicksExpectedMode(
        string type,
        string url,
        HttpTransportMode expected) =>
        Assert.Equal(expected, McpTransportKinds.ResolveHttpTransportMode(type, url));

    [Fact]
    public void FormatHttpTransportLabel_MatchesMode()
    {
        Assert.Equal("sse", McpTransportKinds.FormatHttpTransportLabel(HttpTransportMode.Sse));
        Assert.Equal(
            "streamable-http",
            McpTransportKinds.FormatHttpTransportLabel(HttpTransportMode.StreamableHttp));
        Assert.Equal("http-auto", McpTransportKinds.FormatHttpTransportLabel(HttpTransportMode.AutoDetect));
    }
}
