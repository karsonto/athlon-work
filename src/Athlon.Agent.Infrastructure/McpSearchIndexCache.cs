using Athlon.Agent.Core;

namespace Athlon.Agent.Infrastructure;

internal sealed class McpSearchIndexCache
{
    private int _catalogVersion = -1;
    private CachedIndex? _index;

    public IReadOnlyList<McpSearchIndex.SearchResult> Search(
        IReadOnlyList<McpCatalogEntry> catalog,
        int catalogVersion,
        string query,
        int topK,
        double minScore)
    {
        if (catalog.Count == 0)
        {
            return Array.Empty<McpSearchIndex.SearchResult>();
        }

        if (_index is null || _catalogVersion != catalogVersion)
        {
            _catalogVersion = catalogVersion;
            _index = BuildIndex(catalog);
        }

        return McpSearchIndex.SearchPrepared(_index, query, topK, minScore);
    }

    private static CachedIndex BuildIndex(IReadOnlyList<McpCatalogEntry> catalog)
    {
        var indexed = catalog.Select(McpSearchIndex.IndexEntryPublic).ToArray();
        var documentFrequency = new Dictionary<string, int>(StringComparer.Ordinal);
        var totalTokens = 0;
        foreach (var entry in indexed)
        {
            totalTokens += entry.Tokens.Count;
            foreach (var token in entry.Tokens.Distinct(StringComparer.Ordinal))
            {
                documentFrequency[token] = documentFrequency.GetValueOrDefault(token) + 1;
            }
        }

        var averageLength = Math.Max(1, (double)totalTokens / Math.Max(1, indexed.Length));
        return new CachedIndex(indexed, documentFrequency, averageLength);
    }

    internal sealed record CachedIndex(
        McpSearchIndex.IndexedEntry[] Entries,
        Dictionary<string, int> DocumentFrequency,
        double AverageLength);
}
