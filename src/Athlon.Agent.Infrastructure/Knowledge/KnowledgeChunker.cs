using System.Text;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Knowledge;

namespace Athlon.Agent.Infrastructure.Knowledge;

public sealed class KnowledgeChunker(AppSettings settings)
{
    public IReadOnlyList<KnowledgeChunk> Chunk(string documentId, string moduleId, string text, string title)
    {
        var cfg = settings.Knowledge.Chunking;
        var targetChars = Math.Max(500, cfg.TargetChars);
        var maxChars = Math.Max(targetChars, cfg.MaxChars);
        var overlapChars = Math.Clamp(cfg.OverlapChars, 0, targetChars / 2);
        var chunks = new List<KnowledgeChunk>();
        var current = new StringBuilder();
        var titlePath = title;
        var chunkIndex = 0;

        foreach (var paragraph in EnumerateParagraphs(text))
        {
            if (IsMarkdownHeading(paragraph))
            {
                titlePath = paragraph.TrimStart('#', ' ').Trim();
            }

            if (current.Length > 0 && current.Length + paragraph.Length + 2 > targetChars)
            {
                AddChunk(chunks, documentId, moduleId, chunkIndex++, titlePath, current.ToString(), maxChars);
                var overlap = BuildOverlap(current.ToString(), overlapChars);
                current.Clear();
                if (!string.IsNullOrWhiteSpace(overlap))
                {
                    current.AppendLine(overlap);
                }
            }

            current.AppendLine(paragraph);
            current.AppendLine();
        }

        if (current.Length > 0)
        {
            AddChunk(chunks, documentId, moduleId, chunkIndex, titlePath, current.ToString(), maxChars);
        }

        return chunks;
    }

    private static IEnumerable<string> EnumerateParagraphs(string text)
    {
        foreach (var part in text.Split("\n\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!string.IsNullOrWhiteSpace(part))
            {
                yield return part.Trim();
            }
        }
    }

    private static bool IsMarkdownHeading(string paragraph) =>
        paragraph.StartsWith('#') && paragraph.TakeWhile(ch => ch == '#').Count() <= 6;

    private static void AddChunk(
        List<KnowledgeChunk> chunks,
        string documentId,
        string moduleId,
        int chunkIndex,
        string titlePath,
        string content,
        int maxChars)
    {
        content = content.Trim();
        if (content.Length > maxChars)
        {
            content = content[..maxChars];
        }

        if (string.IsNullOrWhiteSpace(content))
        {
            return;
        }

        chunks.Add(new KnowledgeChunk
        {
            Id = Guid.NewGuid().ToString("N"),
            DocumentId = documentId,
            ModuleId = moduleId,
            ChunkIndex = chunkIndex,
            TitlePath = titlePath,
            Content = content,
            TokenCount = Math.Max(1, content.Length / 4),
            CreatedAt = DateTimeOffset.UtcNow
        });
    }

    private static string BuildOverlap(string content, int overlapChars)
    {
        if (overlapChars <= 0 || content.Length <= overlapChars)
        {
            return string.Empty;
        }

        return content[^overlapChars..];
    }
}
