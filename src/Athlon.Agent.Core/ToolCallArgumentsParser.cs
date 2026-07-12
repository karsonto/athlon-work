using System.Text.Json;

namespace Athlon.Agent.Core;

public static class ToolCallArgumentsParser
{
    public static ToolCallArguments ParseJson(string? argumentsJson) =>
        ToolCallArguments.Parse(argumentsJson);
}
