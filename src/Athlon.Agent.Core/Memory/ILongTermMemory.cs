namespace Athlon.Agent.Core.Memory;

/// <summary>
/// Two-layer file-based long-term memory.
/// Layer 1: memory/YYYY-MM-DD.md (append-only daily ledgers)
/// Layer 2: memory/MEMORY.md     (LLM-consolidated, deduplicated, size-bounded)
/// </summary>
public interface ILongTermMemory
{
    /// <summary>
    /// Reads the current curated MEMORY.md. Returns empty string if none exists.
    /// </summary>
    Task<string> ReadCuratedAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Appends text to today's daily ledger (memory/YYYY-MM-DD.md).
    /// </summary>
    Task AppendDailyAsync(string text, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads today's daily ledger. Returns empty string if none exists.
    /// </summary>
    Task<string> ReadDailyAsync(DateTime date, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists daily ledger files modified after the given watermark (UTC).
    /// Returns file path segments relative to the memory directory (e.g. "2026-06-08.md").
    /// </summary>
    Task<IReadOnlyList<string>> ListDailyFilesAfterAsync(DateTime watermark, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads a daily ledger file by its relative path (e.g. "2026-06-08.md").
    /// </summary>
    Task<string> ReadDailyFileAsync(string relativePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Overwrites MEMORY.md with the consolidated content.
    /// </summary>
    Task WriteCuratedAsync(string content, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads the consolidation watermark (last successful consolidation UTC instant).
    /// Returns DateTime.MinValue when no watermark exists.
    /// </summary>
    Task<DateTime> ReadWatermarkAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes the consolidation watermark.
    /// </summary>
    Task WriteWatermarkAsync(DateTime watermark, CancellationToken cancellationToken = default);

    /// <summary>
    /// Moves a daily file to the archive subdirectory.
    /// </summary>
    Task ArchiveDailyFileAsync(string relativePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists all memory files (MEMORY.md + memory/*.md) for the search tool.
    /// Returns workspace-relative paths.
    /// </summary>
    Task<IReadOnlyList<string>> ListAllMemoryFilePathsAsync(CancellationToken cancellationToken = default);
}
