using Athlon.Agent.Core;
using Athlon.Agent.Infrastructure.Knowledge;

namespace Athlon.Agent.Tests;

public sealed class KnowledgeChunkerTests
{
    [Fact]
    public void Chunk_splits_long_text_without_paragraphs_into_fixed_windows()
    {
        var settings = CreateSettings(targetChars: 500, overlapChars: 0);
        var chunker = new KnowledgeChunker(settings);
        var text = new string('a', 3000);

        var chunks = chunker.Chunk("doc", "module", text, "title");

        Assert.Equal(6, chunks.Count);
        Assert.All(chunks.Take(5), chunk => Assert.Equal(500, chunk.Content.Length));
        Assert.Equal(500, chunks[5].Content.Length);
    }

    [Fact]
    public void Chunk_emits_shorter_final_window()
    {
        var settings = CreateSettings(targetChars: 500, overlapChars: 0);
        var chunker = new KnowledgeChunker(settings);
        var text = new string('b', 1200);

        var chunks = chunker.Chunk("doc", "module", text, "title");

        Assert.Equal(3, chunks.Count);
        Assert.Equal(500, chunks[0].Content.Length);
        Assert.Equal(500, chunks[1].Content.Length);
        Assert.Equal(200, chunks[2].Content.Length);
    }

    [Fact]
    public void Chunk_applies_overlap_between_adjacent_windows()
    {
        var settings = CreateSettings(targetChars: 400, overlapChars: 100);
        var chunker = new KnowledgeChunker(settings);
        var text = new string('c', 1000);

        var chunks = chunker.Chunk("doc", "module", text, "title");

        Assert.True(chunks.Count >= 2);
        Assert.Equal(chunks[0].Content[^100..], chunks[1].Content[..100]);
    }

    [Fact]
    public void Chunk_allows_target_chars_below_legacy_minimum()
    {
        var settings = CreateSettings(targetChars: 200, overlapChars: 0);
        var chunker = new KnowledgeChunker(settings);
        var text = new string('d', 100);

        var chunks = chunker.Chunk("doc", "module", text, "title");

        Assert.Single(chunks);
        Assert.Equal(100, chunks[0].Content.Length);
    }

    [Fact]
    public void Chunk_returns_empty_for_whitespace_only_text()
    {
        var settings = CreateSettings(targetChars: 500, overlapChars: 0);
        var chunker = new KnowledgeChunker(settings);

        var chunks = chunker.Chunk("doc", "module", "   ", "title");

        Assert.Empty(chunks);
    }

    [Fact]
    public void Chunk_uses_document_title_for_all_chunks()
    {
        var settings = CreateSettings(targetChars: 100, overlapChars: 0);
        var chunker = new KnowledgeChunker(settings);
        var text = new string('e', 350);

        var chunks = chunker.Chunk("doc", "module", text, "My Document");

        Assert.Equal(4, chunks.Count);
        Assert.All(chunks, chunk => Assert.Equal("My Document", chunk.TitlePath));
        Assert.Equal([0, 1, 2, 3], chunks.Select(chunk => chunk.ChunkIndex).ToArray());
    }

    private static AppSettings CreateSettings(int targetChars, int overlapChars)
    {
        var settings = new AppSettings();
        settings.Knowledge.Chunking.TargetChars = targetChars;
        settings.Knowledge.Chunking.OverlapChars = overlapChars;
        return settings;
    }
}
