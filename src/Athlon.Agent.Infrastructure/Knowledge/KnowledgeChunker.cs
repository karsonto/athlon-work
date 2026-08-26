using System.Text.RegularExpressions;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Compaction;
using Athlon.Agent.Core.Knowledge;

namespace Athlon.Agent.Infrastructure.Knowledge;

public sealed partial class KnowledgeChunker(AppSettings settings)
{
    [GeneratedRegex(@"^#\s*Page\s+(\d+)\s*$", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex PageHeaderRegex();

    public IReadOnlyList<KnowledgeChunk> Chunk(string documentId, string moduleId, string text, string title)
    {
        text = text.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return [];
        }

        var cfg = settings.Knowledge.Chunking;
        if (cfg.SplitByPage)
        {
            var pages = SplitIntoPages(text);
            if (pages.Count > 0)
            {
                return ChunkPages(documentId, moduleId, title, pages, cfg);
            }
        }

        return ChunkFixedWindow(documentId, moduleId, title, text, pageNumber: null, cfg);
    }

    private static IReadOnlyList<(int PageNumber, string Text)> SplitIntoPages(string text)
    {
        var matches = PageHeaderRegex().Matches(text);
        if (matches.Count == 0)
        {
            return [];
        }

        var pages = new List<(int PageNumber, string Text)>();
        for (var i = 0; i < matches.Count; i++)
        {
            if (!int.TryParse(matches[i].Groups[1].Value, out var pageNumber))
            {
                continue;
            }

            var start = matches[i].Index + matches[i].Length;
            var end = i + 1 < matches.Count ? matches[i + 1].Index : text.Length;
            if (end < start)
            {
                continue;
            }

            var content = text[start..end].Trim();
            if (content.Length == 0)
            {
                continue;
            }

            pages.Add((pageNumber, content));
        }

        return pages;
    }

    private static IReadOnlyList<KnowledgeChunk> ChunkPages(
        string documentId,
        string moduleId,
        string title,
        IReadOnlyList<(int PageNumber, string Text)> pages,
        KnowledgeChunkSettings cfg)
    {
        var chunks = new List<KnowledgeChunk>();
        var chunkIndex = 0;
        foreach (var (pageNumber, pageText) in pages)
        {
            var titlePath = string.IsNullOrWhiteSpace(title)
                ? $"Page {pageNumber}"
                : $"{title} / Page {pageNumber}";
            var pageChunks = ChunkFixedWindow(
                documentId,
                moduleId,
                titlePath,
                pageText,
                pageNumber,
                cfg,
                startIndex: chunkIndex);
            chunks.AddRange(pageChunks);
            chunkIndex += pageChunks.Count;
        }

        return chunks;
    }

    private static IReadOnlyList<KnowledgeChunk> ChunkFixedWindow(
        string documentId,
        string moduleId,
        string titlePath,
        string text,
        int? pageNumber,
        KnowledgeChunkSettings cfg,
        int startIndex = 0)
    {
        // MaxChars is retained in settings for backward compatibility but unused in fixed-window mode.
        var window = Math.Max(1, cfg.TargetChars);
        var overlap = Math.Clamp(cfg.OverlapChars, 0, window / 2);
        var step = Math.Max(1, window - overlap);
        var chunks = new List<KnowledgeChunk>();
        var chunkIndex = startIndex;

        for (var start = 0; start < text.Length; start += step)
        {
            var length = Math.Min(window, text.Length - start);
            var content = text.Substring(start, length);
            if (string.IsNullOrWhiteSpace(content))
            {
                if (start + length >= text.Length)
                {
                    break;
                }

                continue;
            }

            chunks.Add(new KnowledgeChunk
            {
                Id = Guid.NewGuid().ToString("N"),
                DocumentId = documentId,
                ModuleId = moduleId,
                ChunkIndex = chunkIndex++,
                TitlePath = titlePath,
                PageNumber = pageNumber,
                Content = content,
                TokenCount = Math.Max(1, ContextTokenEstimator.EstimateTextTokens(content)),
                CreatedAt = DateTimeOffset.UtcNow
            });

            if (start + length >= text.Length)
            {
                break;
            }
        }

        return chunks;
    }
}
