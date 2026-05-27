using Athlon.Agent.Core;

namespace Athlon.Agent.Tests;

public sealed class McpToolNameCodecTests
{
    [Fact]
    public void EncodeDecode_RoundTrips()
    {
        var name = McpToolNameCodec.Encode("server1", "toolA");
        Assert.True(McpToolNameCodec.TryDecode(name, out var server, out var tool));
        Assert.Equal("server1", server);
        Assert.Equal("toolA", tool);
    }

    [Fact]
    public void TryDecode_RejectsNonMcpNames()
    {
        Assert.False(McpToolNameCodec.TryDecode("file_read", out _, out _));
        Assert.False(McpToolNameCodec.TryDecode("mcp.", out _, out _));
        Assert.False(McpToolNameCodec.TryDecode("mcp.serverOnly", out _, out _));
    }
}

