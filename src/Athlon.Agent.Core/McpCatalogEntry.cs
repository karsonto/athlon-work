namespace Athlon.Agent.Core;

public sealed record McpCatalogEntry(
    string ServerName,
    string ToolName,
    string EncodedName,
    string Description,
    string InputSchemaJson);
