using System.Text.RegularExpressions;
using Athlon.Agent.Core;

namespace Athlon.Agent.Infrastructure;

public static partial class McpSearchIndex
{
    public sealed record SearchResult(McpCatalogEntry Entry, double Score, IReadOnlyList<string> MatchedKeywords);

    public static IReadOnlyList<SearchResult> Search(
        IReadOnlyList<McpCatalogEntry> catalog,
        string query,
        int topK,
        double minScore)
    {
        if (catalog.Count == 0 || string.IsNullOrWhiteSpace(query))
        {
            return Array.Empty<SearchResult>();
        }

        var queryTerms = Tokenize(query);
        if (queryTerms.Count == 0)
        {
            return Array.Empty<SearchResult>();
        }

        var indexed = catalog.Select(IndexEntryPublic).ToArray();
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
        return SearchPrepared(
            new McpSearchIndexCache.CachedIndex(indexed, documentFrequency, averageLength),
            query,
            topK,
            minScore);
    }

    internal static IReadOnlyList<SearchResult> SearchPrepared(
        McpSearchIndexCache.CachedIndex index,
        string query,
        int topK,
        double minScore)
    {
        var queryTerms = Tokenize(query);
        if (queryTerms.Count == 0)
        {
            return Array.Empty<SearchResult>();
        }

        var results = new List<SearchResult>();
        foreach (var entry in index.Entries)
        {
            var keywords = queryTerms.Where(term => entry.TermFrequency.ContainsKey(term)).Distinct(StringComparer.Ordinal).ToArray();
            if (keywords.Length == 0)
            {
                continue;
            }

            var score = Score(entry, queryTerms, index.DocumentFrequency, index.Entries.Length, index.AverageLength);
            if (score < minScore)
            {
                continue;
            }

            results.Add(new SearchResult(entry.Entry, score, keywords));
        }

        return results
            .OrderByDescending(result => result.Score)
            .Take(Math.Max(1, topK))
            .ToArray();
    }

    internal static IndexedEntry IndexEntryPublic(McpCatalogEntry entry) => IndexEntry(entry);

    private static IndexedEntry IndexEntry(McpCatalogEntry entry)
    {
        var text = string.Join(' ',
            entry.ServerName,
            entry.ToolName,
            entry.EncodedName,
            entry.Description,
            entry.InputSchemaJson);
        var tokens = Tokenize(text);
        var termFrequency = tokens
            .GroupBy(token => token, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        return new IndexedEntry(entry, tokens, termFrequency);
    }

    private static double Score(
        IndexedEntry entry,
        IReadOnlyList<string> queryTerms,
        IReadOnlyDictionary<string, int> documentFrequency,
        int totalDocs,
        double averageLength)
    {
        const double k1 = 1.2;
        const double b = 0.75;
        var score = 0.0;
        foreach (var term in queryTerms.Distinct(StringComparer.Ordinal))
        {
            if (!entry.TermFrequency.TryGetValue(term, out var tf))
            {
                continue;
            }

            var df = documentFrequency.GetValueOrDefault(term);
            var idf = Math.Log(1 + (totalDocs - df + 0.5) / (df + 0.5));
            var normalized = (tf * (k1 + 1)) / (tf + k1 * (1 - b + b * (entry.Tokens.Count / averageLength)));
            score += idf * normalized;
        }

        return score;
    }

    internal static List<string> Tokenize(string text)
    {
        var tokens = new List<string>();
        foreach (Match match in LatinTokenPattern().Matches(text.ToLowerInvariant()))
        {
            var term = match.Value;
            if (term.Length >= 2)
            {
                tokens.Add(term);
            }

            foreach (var part in term.Split('_', '-', '.', '/'))
            {
                if (part.Length >= 2)
                {
                    tokens.Add(part);
                }
            }
        }

        foreach (Match match in HanSegmentPattern.Matches(text))
        {
            var chars = match.Value.ToCharArray();
            if (chars.Length == 1)
            {
                tokens.Add(chars[0].ToString());
                continue;
            }

            for (var size = 2; size <= Math.Min(4, chars.Length); size++)
            {
                for (var index = 0; index <= chars.Length - size; index++)
                {
                    tokens.Add(new string(chars, index, size));
                }
            }
        }

        return tokens;
    }

    internal sealed record IndexedEntry(
        McpCatalogEntry Entry,
        List<string> Tokens,
        Dictionary<string, int> TermFrequency);

    [GeneratedRegex("[a-z0-9][a-z0-9_./-]{1,}", RegexOptions.IgnoreCase)]
    private static partial Regex LatinTokenPattern();

    // Runtime Regex: GeneratedRegex does not support \p{Script=Han} (SYSLIB1042).
    private static readonly Regex HanSegmentPattern = new(@"\p{Script=Han}+", RegexOptions.Compiled | RegexOptions.CultureInvariant);
}
