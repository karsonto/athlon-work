using Athlon.Agent.Core;

namespace Athlon.Agent.Tests;

internal static class TestToolDefinitions
{
    public static ToolDefinition Named(string name, string description = "test") =>
        new(name, description, ToolSchema.Object().Build());

    public static ToolDefinition FromLegacy(string name, string description, IReadOnlyDictionary<string, string> parameters) =>
        new(name, description, ToolSchema.FromLegacyParameters(parameters));
}
