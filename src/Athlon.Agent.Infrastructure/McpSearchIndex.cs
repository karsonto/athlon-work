using System.Text.Json;
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
        var serverTokens = Tokenize(entry.ServerName);
        var nameTokens = Tokenize($"{entry.ToolName} {entry.EncodedName}");
        var descriptionTokens = Tokenize(entry.Description);
        var schemaTokens = Tokenize(ExtractSchemaSearchText(entry.InputSchemaJson));
        var tokens = serverTokens.Concat(nameTokens).Concat(descriptionTokens).Concat(schemaTokens).ToList();
        var weightedTermFrequency = new Dictionary<string, double>(StringComparer.Ordinal);
        AddWeighted(weightedTermFrequency, serverTokens, 1.5);
        AddWeighted(weightedTermFrequency, nameTokens, 5.0);
        AddWeighted(weightedTermFrequency, descriptionTokens, 2.5);
        AddWeighted(weightedTermFrequency, schemaTokens, 0.75);
        return new IndexedEntry(
            entry,
            tokens,
            weightedTermFrequency,
            nameTokens.ToHashSet(StringComparer.Ordinal),
            descriptionTokens.ToHashSet(StringComparer.Ordinal));
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
            if (!entry.WeightedTermFrequency.TryGetValue(term, out var tf))
            {
                continue;
            }

            var df = documentFrequency.GetValueOrDefault(term);
            var idf = Math.Log(1 + (totalDocs - df + 0.5) / (df + 0.5));
            var normalized = (tf * (k1 + 1)) / (tf + k1 * (1 - b + b * (entry.Tokens.Count / averageLength)));
            score += idf * normalized;
            if (entry.NameTokens.Contains(term))
            {
                score += idf * 2.0;
            }
            else if (entry.DescriptionTokens.Contains(term))
            {
                score += idf * 0.35;
            }
        }

        var distinctTerms = queryTerms.Distinct(StringComparer.Ordinal).ToArray();
        if (distinctTerms.Length > 1 && distinctTerms.All(entry.NameTokens.Contains))
        {
            score += 4.0;
        }

        return score;
    }

    internal static List<string> Tokenize(string text)
    {
        var tokens = new List<string>();
        foreach (Match match in LatinTokenPattern().Matches(text.ToLowerInvariant()))
        {
            var term = NormalizeToken(match.Value);
            if (term.Length >= 2)
            {
                tokens.Add(term);
            }

            foreach (var part in term.Split('_', '-', '.', '/'))
            {
                if (part.Length >= 2)
                {
                    tokens.Add(NormalizeToken(part));
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

        ExpandSemanticAliases(tokens);
        return tokens;
    }

    internal sealed record IndexedEntry(
        McpCatalogEntry Entry,
        List<string> Tokens,
        Dictionary<string, double> WeightedTermFrequency,
        HashSet<string> NameTokens,
        HashSet<string> DescriptionTokens)
    {
        public IReadOnlyDictionary<string, double> TermFrequency => WeightedTermFrequency;
    }

    private static void AddWeighted(Dictionary<string, double> target, IEnumerable<string> tokens, double weight)
    {
        foreach (var token in tokens)
        {
            target[token] = target.GetValueOrDefault(token) + weight;
        }
    }

    private static string ExtractSchemaSearchText(string schemaJson)
    {
        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(schemaJson) ? "{}" : schemaJson);
            var values = new List<string>();
            CollectSchemaText(document.RootElement, values);
            return string.Join(' ', values);
        }
        catch (JsonException)
        {
            return schemaJson;
        }
    }

    private static void CollectSchemaText(JsonElement element, List<string> values)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                values.Add(property.Name);
                if (property.NameEquals("description") && property.Value.ValueKind == JsonValueKind.String)
                {
                    values.Add(property.Value.GetString() ?? string.Empty);
                }
                else
                {
                    CollectSchemaText(property.Value, values);
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                CollectSchemaText(item, values);
            }
        }
        else if (element.ValueKind == JsonValueKind.String)
        {
            values.Add(element.GetString() ?? string.Empty);
        }
    }

    private static string NormalizeToken(string token)
    {
        token = token.ToLowerInvariant();
        if (token.Length > 4 && token.EndsWith("ies", StringComparison.Ordinal))
        {
            return token[..^3] + "y";
        }

        if (token.Length > 3 && token.EndsWith('s') && !token.EndsWith("ss", StringComparison.Ordinal))
        {
            return token[..^1];
        }

        return token;
    }

    private static void ExpandSemanticAliases(List<string> tokens)
    {
        var snapshot = tokens.Distinct(StringComparer.Ordinal).ToArray();
        foreach (var token in snapshot)
        {
            if (SemanticAliases.TryGetValue(token, out var aliases))
            {
                tokens.AddRange(aliases);
            }
        }
    }

    private static readonly IReadOnlyDictionary<string, string[]> SemanticAliases = BuildSemanticAliases();

    private static IReadOnlyDictionary<string, string[]> BuildSemanticAliases()
    {
        string[][] groups =
        [
            ["search", "find", "query", "搜索", "查找", "查询"],
            ["issue", "ticket", "问题", "工单"],
            ["create", "add", "new", "创建", "新增", "新建"],
            ["send", "post", "发送", "发出"],
            ["analyze", "inspect", "分析", "识别"],
            ["file", "document", "文件", "文档"],
            ["message", "chat", "消息", "聊天"],
            ["browser", "web", "浏览器", "网页"],
            ["navigate", "open", "visit", "导航", "打开", "访问"],
            ["image", "picture", "图片", "图像"],
            ["calendar", "event", "日历", "事件"],
            ["email", "mail", "邮件", "邮箱"]
        ];
        var map = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var group in groups)
        {
            foreach (var term in group)
            {
                map[term] = group.Where(candidate => candidate != term).ToArray();
            }
        }

        return map;
    }

    [GeneratedRegex("[a-z0-9][a-z0-9_./-]{1,}", RegexOptions.IgnoreCase)]
    private static partial Regex LatinTokenPattern();

    // Runtime Regex: avoid \p{Script=Han} — not supported on all .NET regex engines (e.g. net10).
    private static readonly Regex HanSegmentPattern = new(@"[\u3400-\u4DBF\u4E00-\u9FFF\uF900-\uFAFF]+", RegexOptions.Compiled | RegexOptions.CultureInvariant);
}
