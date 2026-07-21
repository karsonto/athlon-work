using Athlon.Agent.Core;
using Athlon.Agent.Core.Memory;
using Athlon.Agent.Infrastructure.Memory;

namespace Athlon.Agent.Tests;

public sealed class MemorySearchToolTests
{
    [Fact]
    public void Tokenize_keeps_underscore_identifiers()
    {
        var tokens = MemorySearchTool.Tokenize("prefer file_edit over apply_patch");
        Assert.Contains("file_edit", tokens);
        Assert.Contains("apply_patch", tokens);
        Assert.DoesNotContain("file", tokens);
        Assert.DoesNotContain("edit", tokens);
    }

    [Fact]
    public void Tokenize_emits_cjk_characters_and_drops_single_latin()
    {
        var tokens = MemorySearchTool.Tokenize("a 偏好 preference");
        Assert.Contains("偏", tokens);
        Assert.Contains("好", tokens);
        Assert.Contains("preference", tokens);
        Assert.DoesNotContain("a", tokens);
    }

    [Fact]
    public void MergeOverlappingHits_collapses_adjacent_windows()
    {
        var hits = new List<MemorySearchTool.LineHit>
        {
            new("MEMORY.md", 5, "alpha", 1.0),
            new("MEMORY.md", 6, "alpha beta", 2.5),
            new("MEMORY.md", 7, "beta", 1.2),
            new("MEMORY.md", 40, "unrelated", 3.0),
        };

        var clusters = MemorySearchTool.MergeOverlappingHits(hits, contextRadius: 3);

        Assert.Equal(2, clusters.Count);
        Assert.Equal(6, clusters[0].PrimaryLine);
        Assert.Equal(2.5, clusters[0].Score);
        Assert.True(clusters[0].WindowStart <= 5);
        Assert.True(clusters[0].WindowEnd >= 7);
        Assert.Equal(40, clusters[1].PrimaryLine);
    }

    [Fact]
    public async Task InvokeAsync_ranks_line_hits_and_omits_score_noise()
    {
        var memory = new StubMemory(
            curated: """
                     # Memory
                     User likes dark mode.
                     Deploy pipeline uses GitHub Actions.
                     Spacer line A.
                     Spacer line B.
                     Spacer line C.
                     Spacer line D.
                     User prefers file_edit for small patches.
                     """,
            daily: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["2026-07-21.md"] = "Notes about lunch only.\n",
            });

        var tool = new MemorySearchTool(memory, new NoOpLogger());
        var result = await tool.InvokeAsync(
            new ToolInvocation("memory_search", new Dictionary<string, string>
            {
                ["query"] = "file_edit",
            }));

        Assert.True(result.Succeeded, result.Error);
        Assert.Contains("> ", result.Content, StringComparison.Ordinal);
        Assert.Contains("file_edit", result.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("[score:", result.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("GitHub", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvokeAsync_supports_cjk_query()
    {
        var memory = new StubMemory(
            curated: "用户偏好深色主题。\n其他杂记。\n",
            daily: new Dictionary<string, string>());

        var tool = new MemorySearchTool(memory, new NoOpLogger());
        var result = await tool.InvokeAsync(
            new ToolInvocation("memory_search", new Dictionary<string, string>
            {
                ["query"] = "偏好",
            }));

        Assert.True(result.Succeeded, result.Error);
        Assert.Contains("深色主题", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvokeAsync_does_not_substring_match_short_terms()
    {
        var memory = new StubMemory(
            curated: """
                     capital city notes
                     spacer a
                     spacer b
                     spacer c
                     spacer d
                     api gateway ready
                     """,
            daily: new Dictionary<string, string>());

        var tool = new MemorySearchTool(memory, new NoOpLogger());
        var result = await tool.InvokeAsync(
            new ToolInvocation("memory_search", new Dictionary<string, string>
            {
                ["query"] = "api",
            }));

        Assert.True(result.Succeeded, result.Error);
        Assert.Contains("> ", result.Content, StringComparison.Ordinal);
        Assert.Contains("api gateway", result.Content, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("capital", result.Content, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class StubMemory(
        string curated,
        Dictionary<string, string> daily) : ILongTermMemory
    {
        public bool HasActiveScope => true;
        public string? ActiveWorkspaceKey => "ws";
        public string? ActiveSessionId => "sess";

        public Task<string> ReadCuratedAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(curated);

        public Task AppendDailyAsync(string text, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<string> ReadDailyAsync(DateTime date, CancellationToken cancellationToken = default) =>
            Task.FromResult(string.Empty);

        public Task<IReadOnlyList<string>> ListDailyFilesAfterAsync(
            DateTime watermark,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());

        public Task<string> ReadDailyFileAsync(string relativePath, CancellationToken cancellationToken = default)
        {
            var name = Path.GetFileName(relativePath);
            return Task.FromResult(daily.TryGetValue(name, out var text) ? text : string.Empty);
        }

        public Task WriteCuratedAsync(string content, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<DateTime> ReadWatermarkAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(DateTime.MinValue);

        public Task WriteWatermarkAsync(DateTime watermark, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task ArchiveDailyFileAsync(string relativePath, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<string>> ListAllMemoryFilePathsAsync(CancellationToken cancellationToken = default)
        {
            var paths = new List<string> { "MEMORY.md" };
            paths.AddRange(daily.Keys);
            return Task.FromResult<IReadOnlyList<string>>(paths);
        }

        public Task DeleteCurrentSessionMemoryAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task DeleteSessionMemoryAsync(
            string? workspaceKey,
            string sessionId,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class NoOpLogger : IAppLogger
    {
        public void Debug(string messageTemplate, params object[] values) { }
        public void Information(string messageTemplate, params object[] values) { }
        public void Warning(string messageTemplate, params object[] values) { }
        public void Error(Exception exception, string messageTemplate, params object[] values) { }
        public IAppLogger ForContext(string context) => this;
    }
}
