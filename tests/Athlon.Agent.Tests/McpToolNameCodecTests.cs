using System.Text.RegularExpressions;
using Athlon.Agent.Core;

namespace Athlon.Agent.Tests;

public sealed class McpToolNameCodecTests
{
    private static readonly Regex ApiToolNamePattern = new("^[a-zA-Z0-9_-]+$", RegexOptions.Compiled);

    [Fact]
    public void EncodeDecode_RoundTrips()
    {
        var name = McpToolNameCodec.Encode("server1", "toolA");
        Assert.Equal("mcp_server1__toolA", name);
        Assert.Matches(ApiToolNamePattern, name);
        Assert.True(McpToolNameCodec.TryDecode(name, out var server, out var tool));
        Assert.Equal("server1", server);
        Assert.Equal("toolA", tool);
    }

    [Fact]
    public void Encode_QwenVision_MatchesApiPattern()
    {
        var name = McpToolNameCodec.Encode("qwen-vision", "analyze_image");
        Assert.Equal("mcp_qwen-vision__analyze_image", name);
        Assert.Matches(ApiToolNamePattern, name);
        Assert.True(McpToolNameCodec.TryDecode(name, out var server, out var tool));
        Assert.Equal("qwen-vision", server);
        Assert.Equal("analyze_image", tool);
    }

    [Fact]
    public void TryDecode_LegacyDotFormat_StillWorks()
    {
        Assert.True(McpToolNameCodec.TryDecode("mcp.demo.echo", out var server, out var tool));
        Assert.Equal("demo", server);
        Assert.Equal("echo", tool);
    }

    [Fact]
    public void TryDecode_RejectsNonMcpNames()
    {
        Assert.False(McpToolNameCodec.TryDecode("file_read", out _, out _));
        Assert.False(McpToolNameCodec.TryDecode("mcp_", out _, out _));
        Assert.False(McpToolNameCodec.TryDecode("mcp_serverOnly", out _, out _));
    }
}
