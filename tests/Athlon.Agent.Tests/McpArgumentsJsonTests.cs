using Athlon.Agent.Mcp;

namespace Athlon.Agent.Tests;

public sealed class McpArgumentsJsonTests
{
    [Fact]
    public void ParseDictionary_ReturnsObjectProperties()
    {
        var arguments = McpArgumentsJson.ParseDictionary("{\"x\":1,\"name\":\"hi\"}");

        Assert.NotNull(arguments);
        Assert.Equal(1, Convert.ToInt64(arguments!["x"]));
        Assert.Equal("hi", arguments["name"]);
    }

    [Fact]
    public void ParseDictionary_ReturnsNullForEmptyOrNonObject()
    {
        Assert.Null(McpArgumentsJson.ParseDictionary(""));
        Assert.Null(McpArgumentsJson.ParseDictionary("[]"));
        Assert.Null(McpArgumentsJson.ParseDictionary("\"raw\""));
    }
}
