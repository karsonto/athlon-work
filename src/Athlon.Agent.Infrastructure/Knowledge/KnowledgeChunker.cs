using Athlon.Agent.Core;
using Athlon.Agent.Core.Compaction;
using Athlon.Agent.Core.Knowledge;

namespace Athlon.Agent.Infrastructure.Knowledge;

public sealed class KnowledgeChunker(AppSettings settings)
{
    public IReadOnlyList<KnowledgeChunk> Chunk(string documentId, string moduleId, string text, string title)
    {
        text = text.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return [];
        }

        var cfg = settings.Knowledge.Chunking;
        // MaxChars is retained in settings for backward compatibility but unused in fixed-window mode.
        var window = Math.Max(1, cfg.TargetChars);
        var overlap = Math.Clamp(cfg.OverlapChars, 0, window / 2);
        var step = Math.Max(1, window - overlap);
        var chunks = new List<KnowledgeChunk>();
        var chunkIndex = 0;

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
                TitlePath = title,
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
