using System.Text;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Knowledge;

namespace Athlon.Agent.Infrastructure.Knowledge;

public sealed class ChatKnowledgeGraphExtractor(
    IAgentModelClient modelClient,
    AppSettings settings,
    IAppLogger logger) : IKnowledgeGraphExtractor
{
    private const string SystemPrompt =
        "You extract a knowledge graph from document chunks. "
        + "Return one JSON object and nothing else. "
        + "Use only the allowed entity types. "
        + "Keep names and descriptions in the source language. "
        + "Do not invent entities that are not supported by the chunk.";

    private readonly IAppLogger _logger = logger.ForContext("KnowledgeGraph");

    public async Task<KnowledgeGraphExtraction> ExtractAsync(
        IReadOnlyList<KnowledgeChunk> chunks,
        IReadOnlyList<string> entityTypes,
        CancellationToken cancellationToken = default)
    {
        if (chunks.Count == 0)
        {
            return new KnowledgeGraphExtraction([], []);
        }

        var types = entityTypes.Count == 0
            ? settings.Knowledge.Graph.NormalizedEntityTypes()
            : entityTypes;
        var batchSize = Math.Clamp(settings.Knowledge.Graph.BatchSize <= 0 ? 4 : settings.Knowledge.Graph.BatchSize, 1, 8);
        var entities = new List<ExtractedKnowledgeEntity>();
        var relations = new List<ExtractedKnowledgeRelation>();
        foreach (var batch in chunks.Chunk(batchSize))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var batchChunks = batch.ToArray();
            var request = new AgentModelRequest(
                [
                    new AgentModelMessage("system", SystemPrompt),
                    new AgentModelMessage("user", BuildUserPrompt(batchChunks, types))
                ],
                Array.Empty<ToolDefinition>(),
                AllowToolCalls: false,
                MaxTokens: 4096);
            var response = await modelClient.CompleteAsync(request, cancellationToken: cancellationToken).ConfigureAwait(false);
            KnowledgeGraphExtraction parsed;
            try
            {
                var chunkIds = batchChunks.Select(chunk => chunk.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
                parsed = KnowledgeGraphExtractionParser.ParseRaw(response.Content ?? "", types, chunkIds);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _logger.Warning("Knowledge graph extraction JSON was rejected: {Message}", exception.Message);
                throw;
            }

            entities.AddRange(parsed.Entities);
            relations.AddRange(parsed.Relations);
        }

        return KnowledgeGraphExtractionParser.Link(entities, relations);
    }

    private static string BuildUserPrompt(IReadOnlyList<KnowledgeChunk> chunks, IReadOnlyList<string> entityTypes)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Allowed entity types: " + string.Join(", ", entityTypes));
        builder.AppendLine("Return JSON with this shape:");
        builder.AppendLine("""
            {"entities":[{"chunkId":"...","name":"...","type":"organization","description":"..."}],"relations":[{"chunkId":"...","source":"...","sourceType":"organization","target":"...","targetType":"person","description":"...","weight":5,"evidence":"..."}]}
            """);
        builder.AppendLine("weight is an integer from 1 to 10. Copy chunkId exactly.");
        builder.AppendLine();
        foreach (var chunk in chunks)
        {
            builder.AppendLine($"chunkId: {chunk.Id}");
            builder.AppendLine(chunk.Content);
            builder.AppendLine();
        }

        return builder.ToString();
    }
}
