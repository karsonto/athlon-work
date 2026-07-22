using Athlon.Agent.App.Services;
using Athlon.Agent.Core;
using Athlon.Agent.Infrastructure;

namespace Athlon.Agent.Tests;

public sealed class McpComposerExpanderTests
{
    [Fact]
    public void Expand_AddsKnownMcpReferenceBlock()
    {
        var encoded = McpToolNameCodec.Encode("demo-server", "browser_navigate");
        var registry = new ComposerTestFactory.ConnectedMcpRegistry("demo-server", "browser_navigate");
        var expanded = McpComposerExpander.Expand($"Use //mcp:{encoded} here.", registry);

        Assert.Contains("[MCP reference:", expanded, StringComparison.Ordinal);
        Assert.Contains($"mcp_call(toolId=\"{encoded}\"", expanded, StringComparison.Ordinal);
        Assert.Contains("arguments={}", expanded, StringComparison.Ordinal);
        Assert.DoesNotContain("argumentsJson", expanded, StringComparison.Ordinal);
        Assert.Contains($"//mcp:{encoded}", expanded, StringComparison.Ordinal);
    }

    [Fact]
    public void Expand_AddsKnownMcpServerReferenceBlock()
    {
        var registry = new ComposerTestFactory.ConnectedMcpRegistry("demo-server", "browser_navigate");
        var expanded = McpComposerExpander.Expand("Use //mcp:demo-server here.", registry);

        Assert.Contains("[MCP server reference: demo-server]", expanded, StringComparison.Ordinal);
        Assert.Contains("Prefer MCP tools from server \"demo-server\"", expanded, StringComparison.Ordinal);
        Assert.Contains("//mcp:demo-server", expanded, StringComparison.Ordinal);
    }

    [Fact]
    public void Expand_AppendsWarningForUnknownMcpReference()
    {
        var expanded = McpComposerExpander.Expand(
            "//mcp:missing__tool",
            new TestMcpRegistry());

        Assert.Contains("Unknown MCP reference 'missing__tool'", expanded, StringComparison.Ordinal);
    }
}
