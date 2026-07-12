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
            var hits = await searchService.SearchAsync(sessionId, query, topK, cancellationToken).ConfigureAwait(false);
            if (hits.Count == 0)
            {
                return ToolResult.Success(
                    "No knowledge hits",
                    "No results found. The current session may have no enabled knowledge modules, or no indexed chunks matched the query.");
            }

            var builder = new StringBuilder();
            for (var i = 0; i < hits.Count; i++)
            {
                var hit = hits[i];
                builder.AppendLine($"[{i + 1}] score={hit.Score.ToString("0.000", CultureInfo.InvariantCulture)}");
                builder.AppendLine($"Source: module={hit.ModuleName}; document={hit.FileName}; title={hit.TitlePath}; page={hit.PageNumber?.ToString(CultureInfo.InvariantCulture) ?? "n/a"}");
                builder.AppendLine(hit.Content.Trim());
                builder.AppendLine();
            }

            return ToolResult.Success($"Found {hits.Count} knowledge hits", builder.ToString().TrimEnd());
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.Warning("Knowledge search failed: {Message}", exception.Message);
            return ToolResult.Failure("Knowledge search failed", exception.Message);
        }
    }
}
