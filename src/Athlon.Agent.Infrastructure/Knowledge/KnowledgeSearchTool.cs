using System.Globalization;
using System.Text;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Knowledge;

namespace Athlon.Agent.Infrastructure.Knowledge;

public sealed class KnowledgeSearchTool(
    IKnowledgeSearchService searchService,
    IActiveAgentSessionContext activeSessionContext,
    IAppLogger logger) : IAgentTool, IGlobalKnowledgeTool
{
    private readonly IAppLogger _logger = logger.ForContext("KnowledgeSearchTool");

    public ToolDefinition Definition => new(
        Name: "knowledge_search",
        Description: "Search the global knowledge base modules explicitly enabled for the current session. Use for questions that may need uploaded reference documents. If no modules are enabled, the tool returns no results.",
        ToolSchema.Object()
            .String("query", "Natural-language search query.", required: true, minLength: 1)
            .Integer("topK", "Max number of chunks to return.", minimum: 1, maximum: 100)
            .Build());

    public async Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken cancellationToken = default)
    {
        if (!invocation.Arguments.TryGetString("query", out var query) || string.IsNullOrWhiteSpace(query))
        {
            return ToolResult.Failure("Missing query", "query parameter is required");
        }

        int? topK = null;
        if (invocation.Arguments.TryGetInt32("topK", out var parsedTopK))
        {
            topK = parsedTopK;
        }

        var sessionId = activeSessionContext.SessionId;
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return ToolResult.Success("No active session", "No active session id is available, so no knowledge modules are enabled.");
        }

        try
        {
            var result = await searchService.SearchAsync(sessionId, query, topK, cancellationToken).ConfigureAwait(false);
            if (result.Hits.Count == 0 && result.Graph.IsEmpty)
            {
                return ToolResult.Success(
                    "No knowledge hits",
                    "No results found. The current session may have no enabled knowledge modules, or no indexed chunks matched the query.");
            }

            var builder = new StringBuilder();
            for (var i = 0; i < result.Hits.Count; i++)
            {
                var hit = result.Hits[i];
                builder.AppendLine($"[{i + 1}] score={hit.Score.ToString("0.000", CultureInfo.InvariantCulture)}");
                builder.AppendLine(FormatSource(hit.ModuleName, hit.FileName, hit.TitlePath, hit.PageNumber));
                builder.AppendLine(hit.Content.Trim());
                builder.AppendLine();
            }

            AppendGraph(builder, result.Graph);
            var summary = result.Hits.Count > 0
                ? $"Found {result.Hits.Count} knowledge hits"
                : "Found graph context";
            return ToolResult.Success(summary, builder.ToString().TrimEnd());
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.Warning("Knowledge search failed: {Message}", exception.Message);
            return ToolResult.Failure("Knowledge search failed", exception.Message);
        }
    }

    private static void AppendGraph(StringBuilder builder, KnowledgeGraphSearchBlock graph)
    {
        if (graph.IsEmpty)
        {
            return;
        }

        builder.AppendLine("Graph");
        builder.AppendLine("Entities");
        foreach (var entity in graph.Entities)
        {
            builder.Append("- ").Append(entity.Name);
            if (!string.IsNullOrWhiteSpace(entity.EntityType))
            {
                builder.Append(" (").Append(entity.EntityType.Trim()).Append(')');
            }

            if (!string.IsNullOrWhiteSpace(entity.Description))
            {
                builder.Append(": ").Append(entity.Description.Trim());
            }

            builder.AppendLine();
        }

        builder.AppendLine("Relations");
        foreach (var relation in graph.Relations)
        {
            var predicate = string.IsNullOrWhiteSpace(relation.Description) ? "相关" : relation.Description.Trim();
            builder.AppendLine($"- {relation.SourceName} —{predicate}→ {relation.TargetName} (weight {relation.Weight.ToString(CultureInfo.InvariantCulture)})");
            builder.AppendLine(FormatSource(relation.ModuleName, relation.FileName, relation.TitlePath, relation.PageNumber));
            if (!string.IsNullOrWhiteSpace(relation.Evidence))
            {
                builder.AppendLine(relation.Evidence.Trim());
            }

            builder.AppendLine();
        }
    }

    private static string FormatSource(string moduleName, string fileName, string titlePath, int? pageNumber) =>
        $"Source: module={moduleName}; document={fileName}; title={titlePath}; page={pageNumber?.ToString(CultureInfo.InvariantCulture) ?? "n/a"}";
}
