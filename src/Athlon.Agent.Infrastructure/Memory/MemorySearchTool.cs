using System.Text;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Memory;

namespace Athlon.Agent.Infrastructure.Memory;

/// <summary>
/// Searches session memory with line-level BM25 ranking, exact token matching,
/// and merged ±N context windows so adjacent hits do not explode token usage.
/// </summary>
public sealed class MemorySearchTool(ILongTermMemory longTermMemory, IAppLogger logger)
    : IAgentTool, ILongTermMemoryTool, IParallelizableAgentTool
{
    private readonly IAppLogger _logger = logger.ForContext("MemorySearchTool");

    private const double K1 = 1.2;
    private const double B = 0.75;
    private const int ContextRadius = 3;
    private const int MaxResults = 30;

    public ToolDefinition Definition => new(
        Name: "memory_search",
        Description:
        "Search through long-term memory files (MEMORY.md and memory/*.md) for relevant information. Use before answering questions about past work, decisions, dates, people, preferences, or todos.",
        ToolSchema.Object()
            .String("query", "Keywords to search for in memory files", required: true, minLength: 1)
            .Build());

    public async Task<ToolResult> InvokeAsync(
        ToolInvocation invocation,
        CancellationToken cancellationToken = default)
    {
        if (!invocation.Arguments.TryGetString("query", out var query) ||
            string.IsNullOrWhiteSpace(query))
            return ToolResult.Failure("No query provided", "query parameter is required");

        try
        {
            var queryTerms = Tokenize(query)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            if (queryTerms.Count == 0)
                return ToolResult.Success("Search completed",
                    $"No meaningful search terms extracted from: {query}");

            var memoryPaths = await longTermMemory.ListAllMemoryFilePathsAsync(cancellationToken);
            var lineDocs = await LoadLineDocumentsAsync(memoryPaths, cancellationToken);
            if (lineDocs.Count == 0)
                return ToolResult.Success("Search completed", "No memory files found to search");

            var scorer = new Bm25Scorer(lineDocs, queryTerms);
            var hits = new List<LineHit>();
            foreach (var line in lineDocs)
            {
                if (!LineContainsAnyTerm(line.Tokens, queryTerms))
                    continue;

                var score = scorer.Score(line);
                if (score <= 0)
                    continue;

                hits.Add(new LineHit(line.Path, line.LineNumber, line.Text, score));
            }

            if (hits.Count == 0)
                return ToolResult.Success("Search completed",
                    $"No matching memories found for: {query}");

            var clusters = MergeOverlappingHits(hits, ContextRadius);
            clusters.Sort((a, b) =>
            {
                var cmp = b.Score.CompareTo(a.Score);
                return cmp != 0 ? cmp : a.PrimaryLine.CompareTo(b.PrimaryLine);
            });

            var linesByPath = lineDocs
                .GroupBy(d => d.Path, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    g => g.Key,
                    g => g.OrderBy(x => x.LineNumber).Select(x => x.Text).ToArray(),
                    StringComparer.OrdinalIgnoreCase);

            var formatted = new List<string>(Math.Min(MaxResults, clusters.Count));
            foreach (var cluster in clusters.Take(MaxResults))
            {
                if (!linesByPath.TryGetValue(cluster.Path, out var fileLines))
                    continue;
                formatted.Add(FormatCluster(cluster, fileLines));
            }

            var summary = clusters.Count <= MaxResults
                ? $"Found {clusters.Count} matches"
                : $"Found {clusters.Count} matches (showing first {MaxResults})";

            return ToolResult.Success(summary, string.Join("\n\n", formatted));
        }
        catch (Exception ex)
        {
            _logger.Warning("Memory search failed: {Error}", ex.Message);
            return ToolResult.Failure("Search failed", ex.Message);
        }
    }

    private async Task<List<LineDocument>> LoadLineDocumentsAsync(
        IReadOnlyList<string> memoryPaths,
        CancellationToken ct)
    {
        var documents = new List<LineDocument>();
        foreach (var relativePath in memoryPaths)
        {
            string content;
            if (string.Equals(relativePath, "MEMORY.md", StringComparison.OrdinalIgnoreCase) ||
                relativePath.EndsWith("/MEMORY.md", StringComparison.OrdinalIgnoreCase) ||
                relativePath.EndsWith("\\MEMORY.md", StringComparison.OrdinalIgnoreCase))
            {
                content = await longTermMemory.ReadCuratedAsync(ct);
            }
            else
            {
                var fileName = relativePath.Split('/', '\\')[^1];
                content = await longTermMemory.ReadDailyFileAsync(fileName, ct);
            }

            if (string.IsNullOrEmpty(content))
                continue;

            var lines = content.Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                var text = lines[i];
                var tokens = Tokenize(text);
                if (tokens.Count == 0)
                    continue;

                documents.Add(new LineDocument(relativePath, i + 1, text, tokens));
            }
        }

        return documents;
    }

    /// <summary>
    /// Tokenizes text for BM25: keeps <c>file_edit</c>-style identifiers intact,
    /// emits one token per CJK ideograph, drops single-letter Latin tokens.
    /// Returns tokens in order with multiplicity (for TF); callers Distinct for queries.
    /// </summary>
    internal static List<string> Tokenize(string text)
    {
        var tokens = new List<string>();
        if (string.IsNullOrWhiteSpace(text))
            return tokens;

        var sb = new StringBuilder();
        void FlushLatin()
        {
            if (sb.Length >= 2)
                tokens.Add(sb.ToString().ToLowerInvariant());
            sb.Clear();
        }

        foreach (var ch in text)
        {
            if (IsCjk(ch))
            {
                FlushLatin();
                tokens.Add(ch.ToString());
                continue;
            }

            if (char.IsLetterOrDigit(ch) || ch is '_' or '-')
            {
                sb.Append(ch);
                continue;
            }

            FlushLatin();
        }

        FlushLatin();
        return tokens;
    }

    /// <summary>
    /// Merges hits whose ±radius windows overlap within the same file.
    /// Keeps the highest-scoring line as the primary pointer for each cluster.
    /// </summary>
    internal static List<HitCluster> MergeOverlappingHits(
        IReadOnlyList<LineHit> hits,
        int contextRadius)
    {
        if (hits.Count == 0)
            return [];

        var ordered = hits
            .OrderBy(h => h.Path, StringComparer.OrdinalIgnoreCase)
            .ThenBy(h => h.LineNumber)
            .ToList();

        var clusters = new List<HitCluster>();
        HitCluster? current = null;

        foreach (var hit in ordered)
        {
            // Windows [L-R, L+R] overlap when nextLine <= current.WindowEnd + R.
            var overlaps = current is not null
                && string.Equals(current.Path, hit.Path, StringComparison.OrdinalIgnoreCase)
                && hit.LineNumber <= current.WindowEnd + contextRadius;

            if (!overlaps || current is null)
            {
                if (current is not null)
                    clusters.Add(current);

                current = new HitCluster(
                    hit.Path,
                    hit.LineNumber,
                    hit.Score,
                    Math.Max(1, hit.LineNumber - contextRadius),
                    hit.LineNumber + contextRadius);
                continue;
            }

            current.WindowEnd = Math.Max(current.WindowEnd, hit.LineNumber + contextRadius);
            if (hit.Score > current.Score)
            {
                current.PrimaryLine = hit.LineNumber;
                current.Score = hit.Score;
            }
        }

        if (current is not null)
            clusters.Add(current);

        return clusters;
    }

    private static bool LineContainsAnyTerm(IReadOnlyList<string> lineTokens, IReadOnlyList<string> queryTerms)
    {
        if (lineTokens.Count == 0 || queryTerms.Count == 0)
            return false;

        var set = new HashSet<string>(lineTokens, StringComparer.Ordinal);
        foreach (var term in queryTerms)
        {
            if (set.Contains(term))
                return true;
        }

        return false;
    }

    private static string FormatCluster(HitCluster cluster, string[] fileLines)
    {
        var start = Math.Max(1, cluster.WindowStart);
        var end = Math.Min(fileLines.Length, cluster.WindowEnd);
        var sb = new StringBuilder();
        sb.AppendLine($"Source: {cluster.Path}#{cluster.PrimaryLine}");
        sb.AppendLine($"Context (lines {start}-{end}):");
        for (var lineNo = start; lineNo <= end; lineNo++)
        {
            var prefix = lineNo == cluster.PrimaryLine ? ">" : " ";
            sb.AppendLine($"{prefix} {lineNo}|{fileLines[lineNo - 1].TrimEnd()}");
        }

        return sb.ToString().TrimEnd();
    }

    private static bool IsCjk(char ch) =>
        ch is (>= '\u3040' and <= '\u30FF') // Hiragana + Katakana
            or (>= '\u3400' and <= '\u4DBF') // CJK Extension A
            or (>= '\u4E00' and <= '\u9FFF') // CJK Unified
            or (>= '\uF900' and <= '\uFAFF'); // CJK Compatibility

    internal sealed class LineHit(string path, int lineNumber, string text, double score)
    {
        public string Path { get; } = path;
        public int LineNumber { get; } = lineNumber;
        public string Text { get; } = text;
        public double Score { get; } = score;
    }

    internal sealed class HitCluster(string path, int primaryLine, double score, int windowStart, int windowEnd)
    {
        public string Path { get; } = path;
        public int PrimaryLine { get; set; } = primaryLine;
        public double Score { get; set; } = score;
        public int WindowStart { get; set; } = windowStart;
        public int WindowEnd { get; set; } = windowEnd;
    }

    private sealed record LineDocument(
        string Path,
        int LineNumber,
        string Text,
        List<string> Tokens);

    private sealed class Bm25Scorer
    {
        private readonly double _averageDocumentLength;
        private readonly int _totalDocuments;
        private readonly Dictionary<string, double> _idfCache = new(StringComparer.Ordinal);
        private readonly List<string> _queryTerms;

        public Bm25Scorer(List<LineDocument> documents, List<string> queryTerms)
        {
            _queryTerms = queryTerms;
            _totalDocuments = documents.Count;
            if (_totalDocuments == 0)
            {
                _averageDocumentLength = 0;
                return;
            }

            _averageDocumentLength = documents.Average(d => (double)d.Tokens.Count);

            foreach (var term in queryTerms)
            {
                var docsContainingTerm = documents.Count(d =>
                    d.Tokens.Contains(term, StringComparer.Ordinal));
                var idf = Math.Log(
                    (_totalDocuments - docsContainingTerm + 0.5) /
                    (docsContainingTerm + 0.5) + 1.0);
                _idfCache[term] = idf;
            }
        }

        public double Score(LineDocument doc)
        {
            if (_totalDocuments == 0 || _averageDocumentLength <= 0 || doc.Tokens.Count == 0)
                return 0;

            var termFreq = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var token in doc.Tokens)
            {
                termFreq.TryGetValue(token, out var count);
                termFreq[token] = count + 1;
            }

            double score = 0;
            double docLen = doc.Tokens.Count;
            foreach (var term in _queryTerms)
            {
                if (!_idfCache.TryGetValue(term, out var idf))
                    continue;
                if (!termFreq.TryGetValue(term, out var tf) || tf == 0)
                    continue;

                score += idf * (tf * (K1 + 1)) /
                         (tf + K1 * (1 - B + B * docLen / _averageDocumentLength));
            }

            return score;
        }
    }
}
