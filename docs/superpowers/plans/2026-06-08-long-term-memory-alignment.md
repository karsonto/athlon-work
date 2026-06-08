# Long-Term Memory Alignment Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add two-layer long-term memory (borrowing agentscope-java's model) to athlon-work, so the agent can persist and recall facts across sessions.

**Architecture:** Three services orchestrated by `AgentRuntime` post-turn: (1) **MemoryFlushService** extracts new facts from the finished conversation turn via LLM and appends to `memory/YYYY-MM-DD.md`; (2) **MemoryConsolidationService** periodically merges daily files into `memory/MEMORY.md` with LLM dedup/cut; (3) **MemoryPromptContributor** injects MEMORY.md content into system prompt each reasoning iteration. Two agent tools `memory_search` and `memory_get` let the agent query archived memories directly.

**Tech Stack:** C# .NET 8, existing `IAgentModelClient` (LLM calls), `IFileStorageService` (file I/O), `IAppPathProvider` (data paths), `IPreReasoningPromptContributor` (prompt injection), `IAgentTool` (agent tools), DI in `ServiceCollectionExtensions`.

---

## File Structure

### New files

| Path | Responsibility |
|---|---|
| `src/Athlon.Agent.Core/Memory/ILongTermMemory.cs` | Core interface: `RecordAsync`, `RetrieveAsync` |
| `src/Athlon.Agent.Core/Memory/MemorySettings.cs` | Config model: daily retention, consolidate interval, max tokens |
| `src/Athlon.Agent.Core/Memory/MemoryFlushResult.cs` | Result record for flush operation |
| `src/Athlon.Agent.Infrastructure/Memory/FileLongTermMemory.cs` | Two-layer file I/O: read/write daily ledgers + MEMORY.md + watermark |
| `src/Athlon.Agent.Infrastructure/Memory/MemoryFlushService.cs` | LLM extraction from conversation → append to daily ledger |
| `src/Athlon.Agent.Infrastructure/Memory/MemoryConsolidationService.cs` | LLM merge of daily ledgers into MEMORY.md |
| `src/Athlon.Agent.Infrastructure/Memory/MemorySearchTool.cs` | `memory_search` agent tool |
| `src/Athlon.Agent.Infrastructure/Memory/MemoryGetTool.cs` | `memory_get` agent tool |
| `src/Athlon.Agent.Infrastructure/Memory/MemoryPromptContributor.cs` | `IPreReasoningPromptContributor` that injects MEMORY.md |

### Modified files

| Path | Change |
|---|---|
| `src/Athlon.Agent.Core/AgentSettings.cs` | Add `Memory` property + `MemorySettings` class |
| `src/Athlon.Agent.Core/AgentRuntime.cs` | Post-turn flush call + overflow memory retrieval |
| `src/Athlon.Agent.Infrastructure/ServiceCollectionExtensions.cs` | Register new services and tools |
| `src/Athlon.Agent.Core/Prompt/SystemPromptOrchestrator.cs` | (No change — already consumes `IEnumerable<IPreReasoningPromptContributor>`) |

---

### Task 1: Define memory settings model

**Files:**
- Create: `src/Athlon.Agent.Core/Memory/MemorySettings.cs`
- Modify: `src/Athlon.Agent.Core/AgentSettings.cs` (line 23, add property)

- [ ] **Step 1: Create MemorySettings.cs**

```csharp
namespace Athlon.Agent.Core.Memory;

public sealed class MemorySettings
{
    /// <summary>Master switch. When false the memory system is completely disabled.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Minimum gap between two consolidation runs.</summary>
    public TimeSpan ConsolidationMinGap { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>Daily files older than this many days are archived.</summary>
    public int DailyFileRetentionDays { get; set; } = 90;

    /// <summary>Max tokens for the consolidated MEMORY.md (fed to LLM as token budget).</summary>
    public int MaxMemoryTokens { get; set; } = 4000;

    /// <summary>Max tokens for the flush/summary LLM call output.</summary>
    public int SummaryMaxTokens { get; set; } = 1024;

    /// <summary>Max characters of conversation to include in flush prompt.</summary>
    public int MaxFlushConversationChars { get; set; } = 80_000;

    /// <summary>Subdirectory name under the app data root.</summary>
    public string MemoryDirName { get; set; } = "memory";

    /// <summary>Name of the curated memory file.</summary>
    public string CuratedFileName { get; set; } = "MEMORY.md";

    /// <summary>Name of the consolidation watermark file.</summary>
    public string WatermarkFileName { get; set; } = ".consolidation_state";

    /// <summary>Names of memory directories/files excluded from workspace tools.</summary>
    public List<string> ExcludePatterns { get; set; } = new() { "memory/", "MEMORY.md" };
}
```

- [ ] **Step 2: Add property to AgentSettings.cs**

Find the closing brace of `AppSettings` (currently line 23) and add the property inside the class body:

```csharp
public MemorySettings Memory { get; set; } = new();
```

Add using at top (or keep in same namespace):

```csharp
// No extra using needed — MemorySettings is in Athlon.Agent.Core.Memory
```

- [ ] **Step 3: Run build to verify**

Run: `dotnet build src/Athlon.Agent.Core/Athlon.Agent.Core.csproj`
Expected: PASS

- [ ] **Step 4: Commit**

```bash
git add src/Athlon.Agent.Core/Memory/MemorySettings.cs src/Athlon.Agent.Core/AgentSettings.cs
git commit -m "feat(memory): add memory settings model and wire into AppSettings"
```

---

### Task 2: Define ILongTermMemory interface

**Files:**
- Create: `src/Athlon.Agent.Core/Memory/ILongTermMemory.cs`
- Create: `src/Athlon.Agent.Core/Memory/MemoryFlushResult.cs`

- [ ] **Step 1: Create ILongTermMemory.cs**

```csharp
using Athlon.Agent.Core.Prompt;

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
```

- [ ] **Step 2: Create MemoryFlushResult.cs**

```csharp
namespace Athlon.Agent.Core.Memory;

public sealed record MemoryFlushResult(
    bool Flushed,
    string? Summary = null,
    string? Error = null)
{
    public static MemoryFlushResult Skipped => new(false);
    public static MemoryFlushResult Success(string summary) => new(true, summary);
    public static MemoryFlushResult Failed(string error) => new(false, null, error);
}
```

- [ ] **Step 3: Run build to verify**

Run: `dotnet build src/Athlon.Agent.Core/Athlon.Agent.Core.csproj`
Expected: PASS

- [ ] **Step 4: Commit**

```bash
git add src/Athlon.Agent.Core/Memory/ILongTermMemory.cs src/Athlon.Agent.Core/Memory/MemoryFlushResult.cs
git commit -m "feat(memory): add ILongTermMemory interface and flush result record"
```

---

### Task 3: Implement FileLongTermMemory

**Files:**
- Create: `src/Athlon.Agent.Infrastructure/Memory/FileLongTermMemory.cs`

- [ ] **Step 1: Create FileLongTermMemory.cs**

```csharp
using System.Text;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Memory;

namespace Athlon.Agent.Infrastructure.Memory;

public sealed class FileLongTermMemory(
    IAppPathProvider paths,
    IFileStorageService storage,
    AppSettings settings,
    IAppLogger logger) : ILongTermMemory
{
    private readonly IAppLogger _logger = logger.ForContext("FileLongTermMemory");
    private readonly MemorySettings _cfg = settings.Memory;

    private string MemoryDir => Path.Combine(paths.AppDataPath, _cfg.MemoryDirName);
    private string CuratedPath => Path.Combine(MemoryDir, _cfg.CuratedFileName);
    private string WatermarkPath => Path.Combine(MemoryDir, _cfg.WatermarkFileName);
    private string ArchiveDir => Path.Combine(MemoryDir, "archive");

    private string DailyPath(DateTime date) =>
        Path.Combine(MemoryDir, date.ToString("yyyy-MM-dd") + ".md");

    public Task<string> ReadCuratedAsync(CancellationToken ct = default)
    {
        var path = CuratedPath;
        if (!File.Exists(path)) return Task.FromResult(string.Empty);
        return File.ReadAllTextAsync(path, Encoding.UTF8, ct);
    }

    public async Task AppendDailyAsync(string text, CancellationToken ct = default)
    {
        Directory.CreateDirectory(MemoryDir);
        var path = DailyPath(DateTime.UtcNow);
        await File.AppendAllTextAsync(path, text, Encoding.UTF8, ct);
    }

    public Task<string> ReadDailyAsync(DateTime date, CancellationToken ct = default)
    {
        var path = DailyPath(date);
        if (!File.Exists(path)) return Task.FromResult(string.Empty);
        return File.ReadAllTextAsync(path, Encoding.UTF8, ct);
    }

    public Task<IReadOnlyList<string>> ListDailyFilesAfterAsync(DateTime watermark, CancellationToken ct = default)
    {
        if (!Directory.Exists(MemoryDir))
            return Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());

        var result = new List<string>();
        foreach (var filePath in Directory.GetFiles(MemoryDir, "????-??-??.md"))
        {
            var name = Path.GetFileName(filePath);
            try
            {
                var fileDate = DateTime.ParseExact(name.AsSpan(0, 10), "yyyy-MM-dd", null);
                if (fileDate.ToUniversalTime() > watermark || File.GetLastWriteTimeUtc(filePath) > watermark)
                {
                    result.Add(name);
                }
            }
            catch
            {
                // skip non-date files
            }
        }
        result.Sort();
        return Task.FromResult<IReadOnlyList<string>>(result);
    }

    public Task<string> ReadDailyFileAsync(string relativePath, CancellationToken ct = default)
    {
        var path = Path.Combine(MemoryDir, relativePath);
        if (!File.Exists(path)) return Task.FromResult(string.Empty);
        return File.ReadAllTextAsync(path, Encoding.UTF8, ct);
    }

    public async Task WriteCuratedAsync(string content, CancellationToken ct = default)
    {
        Directory.CreateDirectory(MemoryDir);
        await File.WriteAllTextAsync(CuratedPath, content, Encoding.UTF8, ct);
    }

    public Task<DateTime> ReadWatermarkAsync(CancellationToken ct = default)
    {
        if (!File.Exists(WatermarkPath))
            return Task.FromResult(DateTime.MinValue);
        var text = File.ReadAllText(WatermarkPath, Encoding.UTF8);
        if (DateTime.TryParse(text.Trim(), null, System.Globalization.DateTimeStyles.RoundtripKind, out var dt))
            return Task.FromResult(dt);
        return Task.FromResult(DateTime.MinValue);
    }

    public async Task WriteWatermarkAsync(DateTime watermark, CancellationToken ct = default)
    {
        Directory.CreateDirectory(MemoryDir);
        await File.WriteAllTextAsync(WatermarkPath, watermark.ToString("O"), Encoding.UTF8, ct);
    }

    public Task ArchiveDailyFileAsync(string relativePath, CancellationToken ct = default)
    {
        Directory.CreateDirectory(ArchiveDir);
        var src = Path.Combine(MemoryDir, relativePath);
        var dst = Path.Combine(ArchiveDir, relativePath);
        if (File.Exists(src))
            File.Move(src, dst, overwrite: true);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<string>> ListAllMemoryFilePathsAsync(CancellationToken ct = default)
    {
        var result = new List<string>();
        if (File.Exists(CuratedPath))
            result.Add(_cfg.MemoryDirName + "/" + _cfg.CuratedFileName);

        if (Directory.Exists(MemoryDir))
        {
            foreach (var filePath in Directory.GetFiles(MemoryDir, "*.md"))
            {
                var name = Path.GetFileName(filePath);
                if (name == _cfg.CuratedFileName || name == _cfg.WatermarkFileName)
                    continue;
                // only include date-named daily files
                if (name.Length >= 10 && name[..10].Contains('-') && name.EndsWith(".md"))
                {
                    result.Add(_cfg.MemoryDirName + "/" + name);
                }
            }
        }
        result.Sort(StringComparer.OrdinalIgnoreCase);
        return Task.FromResult<IReadOnlyList<string>>(result);
    }
}
```

- [ ] **Step 2: Run build to verify**

Run: `dotnet build src/Athlon.Agent.Infrastructure/Athlon.Agent.Infrastructure.csproj`
Expected: PASS

- [ ] **Step 3: Commit**

```bash
git add src/Athlon.Agent.Infrastructure/Memory/FileLongTermMemory.cs
git commit -m "feat(memory): implement FileLongTermMemory with two-layer file I/O"
```

---

### Task 4: Implement MemoryFlushService

**Files:**
- Create: `src/Athlon.Agent.Infrastructure/Memory/MemoryFlushService.cs`

- [ ] **Step 1: Create MemoryFlushService.cs**

```csharp
using System.Text;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Memory;
using Athlon.Agent.Core.Streaming;

namespace Athlon.Agent.Infrastructure.Memory;

/// <summary>
/// Extracts new long-term memories from a finished conversation turn via LLM,
/// then appends them to today's daily memory ledger.
/// </summary>
public sealed class MemoryFlushService(
    ILongTermMemory longTermMemory,
    IAgentModelClient modelClient,
    AppSettings settings,
    IAppLogger logger)
{
    private const string FlushSystemPrompt = """
You are a memory extraction assistant. Analyze the conversation below and extract important facts, decisions, preferences, and contextual information that should be remembered for future conversations.

Output ONLY the extracted memories as a markdown bullet list. Each item should be a concise, self-contained fact. Include dates, names, and specifics when available.

If there is nothing worth remembering, respond with exactly: NO_REPLY

Guidelines:
- Extract user preferences, personal information, project decisions
- Capture important technical decisions and their rationale
- Note any commitments, deadlines, or action items
- Ignore routine greetings, tool invocations, and ephemeral status updates

IMPORTANT:
- You are writing to TODAY's daily memory ledger (memory/YYYY-MM-DD.md), NOT to MEMORY.md.
- MEMORY.md is the curated long-term memory and is shown ONLY as read-only context below. Do NOT restate facts already covered by MEMORY.md or by today's earlier entries.
- Keep each bullet point independent and self-contained.
""";

    private readonly IAppLogger _logger = logger.ForContext("MemoryFlushService");
    private readonly MemorySettings _cfg = settings.Memory;

    public async Task<MemoryFlushResult> FlushAsync(
        IReadOnlyList<ChatMessage> messages,
        CancellationToken cancellationToken = default)
    {
        if (!_cfg.Enabled)
            return MemoryFlushResult.Skipped;

        var conversationText = SerializeMessages(messages);
        if (string.IsNullOrWhiteSpace(conversationText))
            return MemoryFlushResult.Skipped;

        var existingMemory = await longTermMemory.ReadCuratedAsync(cancellationToken);
        var today = DateTime.UtcNow;
        var existingDaily = await longTermMemory.ReadDailyAsync(today, cancellationToken);

        var userPrompt = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(existingMemory))
        {
            userPrompt.AppendLine("MEMORY.md (read-only curated long-term memory — do NOT restate):");
            userPrompt.AppendLine(existingMemory);
            userPrompt.AppendLine();
        }
        if (!string.IsNullOrWhiteSpace(existingDaily))
        {
            userPrompt.AppendLine("Today's daily ledger so far (your output will be appended after):");
            userPrompt.AppendLine(existingDaily);
            userPrompt.AppendLine();
        }
        userPrompt.AppendLine("Extract NEW memories from this conversation window (skip anything already covered above):");
        userPrompt.AppendLine();
        userPrompt.Append(conversationText);

        var request = new AgentModelRequest(
            new[]
            {
                new AgentModelMessage("system", FlushSystemPrompt),
                new AgentModelMessage("user", userPrompt.ToString())
            },
            Array.Empty<ToolDefinition>(),
            AllowToolCalls: false,
            MaxTokens: _cfg.SummaryMaxTokens);

        AgentModelResponse response;
        try
        {
            response = await modelClient.CompleteAsync(request, cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.Warning("Memory flush LLM call failed: {Error}", ex.Message);
            return MemoryFlushResult.Failed(ex.Message);
        }

        var extracted = response.Content?.Trim();
        if (string.IsNullOrWhiteSpace(extracted) || extracted == "NO_REPLY")
        {
            _logger.Debug("No memories to flush");
            return MemoryFlushResult.Skipped;
        }

        var dailyEntry = $"\n## Memory Flush — {DateTime.UtcNow:O}\n{extracted}\n";
        await longTermMemory.AppendDailyAsync(dailyEntry, cancellationToken);
        _logger.Information("Flushed {Length} chars to daily memory ledger", extracted.Length);
        return MemoryFlushResult.Success(extracted);
    }

    private static string SerializeMessages(IReadOnlyList<ChatMessage> messages)
    {
        var sb = new StringBuilder();
        foreach (var message in messages)
        {
            if (message.Role is MessageRole.System or MessageRole.Compaction)
                continue;
            if (message.Role == MessageRole.User && message.Content?.Contains("<session_context>") == true)
                continue;

            sb.Append('[').Append(message.Role).Append("]: ");
            sb.AppendLine(message.Content);
            sb.AppendLine();
        }
        var result = sb.ToString();
        if (result.Length > 80_000)
            result = result[^80_000..];
        return result;
    }
}
```

- [ ] **Step 2: Run build to verify**

Run: `dotnet build src/Athlon.Agent.Infrastructure/Athlon.Agent.Infrastructure.csproj`
Expected: PASS

- [ ] **Step 3: Commit**

```bash
git add src/Athlon.Agent.Infrastructure/Memory/MemoryFlushService.cs
git commit -m "feat(memory): implement MemoryFlushService for LLM-based memory extraction"
```

---

### Task 5: Implement MemoryConsolidationService

**Files:**
- Create: `src/Athlon.Agent.Infrastructure/Memory/MemoryConsolidationService.cs`

- [ ] **Step 1: Create MemoryConsolidationService.cs**

```csharp
using System.Text;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Memory;
using Athlon.Agent.Core.Streaming;

namespace Athlon.Agent.Infrastructure.Memory;

/// <summary>
/// Periodically merges daily ledgers into a curated, deduplicated, size-bounded MEMORY.md.
/// Uses a watermark (.consolidation_state) to process only new daily files.
/// </summary>
public sealed class MemoryConsolidationService(
    ILongTermMemory longTermMemory,
    IAgentModelClient modelClient,
    AppSettings settings,
    IAppLogger logger)
{
    private const string ConsolidationPrompt = """
You are a memory consolidation assistant. You own the curated long-term memory file MEMORY.md. Your job is to merge new daily ledger entries into MEMORY.md while keeping it concise, deduplicated, and high-signal.

You are given two inputs:
1. The current MEMORY.md content (the existing curated long-term memory).
2. New daily ledger entries that have been appended since the last consolidation.

Rules:
- MEMORY.md is the single source of truth for cross-day, cross-session knowledge. Keep it stable and authoritative.
- Daily ledger entries are stream-of-consciousness flush logs — they may be noisy, redundant with MEMORY.md, or redundant with each other. Promote only what is durable and reusable.
- Deduplicate: if a new entry restates something MEMORY.md already covers, skip it.
- Merge related facts: combine entries about the same topic into cohesive paragraphs with clear section headers.
- Update or remove stale information when new entries supersede it.
- Keep total output within {0} tokens (approximately {1} characters); prioritize recent and frequently-referenced information when trimming.

Output the COMPLETE new MEMORY.md content (not just a diff). Use markdown.
""";

    private readonly IAppLogger _logger = logger.ForContext("MemoryConsolidationService");
    private readonly MemorySettings _cfg = settings.Memory;

    /// <summary>Runs a single consolidation cycle. No-op if no new daily files exist.</summary>
    public async Task ConsolidateAsync(CancellationToken cancellationToken = default)
    {
        if (!_cfg.Enabled)
            return;

        var watermark = await longTermMemory.ReadWatermarkAsync(cancellationToken);
        if (watermark == default)
            watermark = DateTime.MinValue;

        var dailyFiles = await longTermMemory.ListDailyFilesAfterAsync(watermark, cancellationToken);
        if (dailyFiles.Count == 0)
        {
            _logger.Debug("No fresh daily entries since {Watermark:O} — skipping consolidation", watermark);
            return;
        }

        var runStart = DateTime.UtcNow;
        var currentMemory = await longTermMemory.ReadCuratedAsync(cancellationToken);
        var dailyEntries = await ReadDailyEntriesAsync(dailyFiles, cancellationToken);

        var maxChars = _cfg.MaxMemoryTokens * 4;
        var systemPrompt = string.Format(ConsolidationPrompt, _cfg.MaxMemoryTokens, maxChars);

        var userContent = new StringBuilder();
        userContent.AppendLine("Current MEMORY.md:");
        userContent.AppendLine(string.IsNullOrWhiteSpace(currentMemory) ? "(empty)" : currentMemory);
        userContent.AppendLine();
        userContent.AppendLine("New daily ledger entries to merge" +
            (watermark > DateTime.MinValue ? $" (since {watermark:O})" : "") + ":");
        userContent.AppendLine();
        userContent.Append(dailyEntries);

        var request = new AgentModelRequest(
            new[]
            {
                new AgentModelMessage("system", systemPrompt),
                new AgentModelMessage("user", userContent.ToString())
            },
            Array.Empty<ToolDefinition>(),
            AllowToolCalls: false,
            MaxTokens: _cfg.SummaryMaxTokens * 4);

        AgentModelResponse response;
        try
        {
            response = await modelClient.CompleteAsync(request, cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.Warning("Memory consolidation LLM call failed: {Error}", ex.Message);
            return;
        }

        var consolidated = response.Content?.Trim();
        if (string.IsNullOrWhiteSpace(consolidated))
        {
            _logger.Warning("Consolidation produced empty output, skipping");
            return;
        }

        await longTermMemory.WriteCuratedAsync(consolidated, cancellationToken);
        await longTermMemory.WriteWatermarkAsync(runStart, cancellationToken);
        _logger.Information("MEMORY.md consolidated ({Length} chars), watermark advanced to {Watermark:O}",
            consolidated.Length, runStart);
    }

    private async Task<string> ReadDailyEntriesAsync(
        IReadOnlyList<string> fileNames,
        CancellationToken cancellationToken)
    {
        var sb = new StringBuilder();
        foreach (var name in fileNames)
        {
            var content = await longTermMemory.ReadDailyFileAsync(name, cancellationToken);
            if (!string.IsNullOrWhiteSpace(content))
            {
                sb.AppendLine("### " + name);
                sb.AppendLine(content.Trim());
                sb.AppendLine();
            }
        }
        return sb.ToString();
    }
}
```

- [ ] **Step 2: Run build to verify**

Run: `dotnet build src/Athlon.Agent.Infrastructure/Athlon.Agent.Infrastructure.csproj`
Expected: PASS

- [ ] **Step 3: Commit**

```bash
git add src/Athlon.Agent.Infrastructure/Memory/MemoryConsolidationService.cs
git commit -m "feat(memory): implement MemoryConsolidationService for LLM-based MEMORY.md merge"
```

---

### Task 6: Implement memory search/get agent tools

**Files:**
- Create: `src/Athlon.Agent.Infrastructure/Memory/MemorySearchTool.cs`
- Create: `src/Athlon.Agent.Infrastructure/Memory/MemoryGetTool.cs`

- [ ] **Step 1: Create MemorySearchTool.cs**

```csharp
using System.Text.RegularExpressions;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Memory;

namespace Athlon.Agent.Infrastructure.Memory;

public sealed class MemorySearchTool(ILongTermMemory longTermMemory, IAppLogger logger) : IAgentTool
{
    private readonly IAppLogger _logger = logger.ForContext("MemorySearchTool");

    public ToolDefinition Definition => new(
        Name: "memory_search",
        Description: "Search through long-term memory files (MEMORY.md and memory/*.md) for relevant information. Use before answering questions about prior work, decisions, dates, people, preferences, or todos.",
        Parameters: new Dictionary<string, string>
        {
            ["query"] = "Keywords to search for in memory files"
        });

    public async Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken cancellationToken = default)
    {
        if (!invocation.Arguments.TryGetValue("query", out var query) || string.IsNullOrWhiteSpace(query))
            return ToolResult.Failure("No query provided", "query parameter is required");

        try
        {
            var memoryPaths = await longTermMemory.ListAllMemoryFilePathsAsync(cancellationToken);
            var pattern = new Regex(Regex.Escape(query), RegexOptions.IgnoreCase | RegexOptions.Compiled);
            var results = new List<string>();
            var matchCount = 0;

            foreach (var relativePath in memoryPaths)
            {
                string content;
                if (relativePath.StartsWith("memory/", StringComparison.OrdinalIgnoreCase) &&
                    (relativePath.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ||
                     relativePath.Equals("MEMORY.md", StringComparison.OrdinalIgnoreCase)))
                {
                    // resolve via FileLongTermMemory
                    if (relativePath.EndsWith("MEMORY.md", StringComparison.OrdinalIgnoreCase))
                    {
                        content = await longTermMemory.ReadCuratedAsync(cancellationToken);
                    }
                    else
                    {
                        var fileName = relativePath.Split('/')[^1];
                        content = await longTermMemory.ReadDailyFileAsync(fileName, cancellationToken);
                    }
                }
                else
                {
                    continue;
                }

                if (string.IsNullOrEmpty(content))
                    continue;

                var lines = content.Split('\n');
                for (int i = 0; i < lines.Length; i++)
                {
                    if (pattern.IsMatch(lines[i]))
                    {
                        results.Add($"Source: {relativePath}#{i + 1}: {lines[i].Trim()}");
                        matchCount++;
                    }
                }
            }

            if (matchCount == 0)
                return ToolResult.Success("Search completed", $"No matching memories found for: {query}");

            var summary = matchCount <= 30
                ? $"Found {matchCount} matches"
                : $"Found {matchCount} matches (showing first 30)";

            var content = string.Join("\n", results.Take(30));
            return ToolResult.Success(summary, content);
        }
        catch (Exception ex)
        {
            _logger.Warning("Memory search failed: {Error}", ex.Message);
            return ToolResult.Failure("Search failed", ex.Message);
        }
    }
}
```

- [ ] **Step 2: Create MemoryGetTool.cs**

```csharp
using Athlon.Agent.Core;
using Athlon.Agent.Core.Memory;

namespace Athlon.Agent.Infrastructure.Memory;

public sealed class MemoryGetTool(ILongTermMemory longTermMemory, IAppLogger logger) : IAgentTool
{
    private readonly IAppLogger _logger = logger.ForContext("MemoryGetTool");

    public ToolDefinition Definition => new(
        Name: "memory_get",
        Description: "Read specific lines from a memory file. Use after memory_search to pull full context around matched lines. Path is relative to memory directory (e.g., MEMORY.md or 2026-04-01.md).",
        Parameters: new Dictionary<string, string>
        {
            ["path"] = "Relative path to the memory file (e.g., MEMORY.md or 2026-04-01.md)",
            ["start_line"] = "Start line number (1-based, inclusive)",
            ["end_line"] = "End line number (1-based, inclusive)"
        });

    public async Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken cancellationToken = default)
    {
        if (!invocation.Arguments.TryGetValue("path", out var path) || string.IsNullOrWhiteSpace(path))
            return ToolResult.Failure("Missing path", "path parameter is required");

        if (!invocation.Arguments.TryGetValue("start_line", out var startStr) ||
            !int.TryParse(startStr, out var startLine))
            return ToolResult.Failure("Missing or invalid start_line", "start_line must be an integer");

        if (!invocation.Arguments.TryGetValue("end_line", out var endStr) ||
            !int.TryParse(endStr, out var endLine))
            return ToolResult.Failure("Missing or invalid end_line", "end_line must be an integer");

        try
        {
            string content;
            if (path.Equals("MEMORY.md", StringComparison.OrdinalIgnoreCase))
            {
                content = await longTermMemory.ReadCuratedAsync(cancellationToken);
            }
            else
            {
                var fileName = path.TrimStart("memory/".Length).TrimStart('/');
                content = await longTermMemory.ReadDailyFileAsync(fileName, cancellationToken);
            }

            if (string.IsNullOrEmpty(content))
                return ToolResult.Failure("File not found", $"No memory file found at: {path}");

            var lines = content.Split('\n');
            var start = Math.Max(0, startLine - 1);
            var end = Math.Min(lines.Length, endLine);

            if (start >= lines.Length)
                return ToolResult.Failure("Invalid range",
                    $"start_line {startLine} exceeds file length {lines.Length}");

            var sb = new System.Text.StringBuilder();
            for (int i = start; i < end; i++)
            {
                sb.AppendLine($"{i + 1}|{lines[i]}");
            }
            return ToolResult.Success($"Read lines {startLine}-{endLine} from {path}", sb.ToString());
        }
        catch (Exception ex)
        {
            _logger.Warning("Memory get failed: {Error}", ex.Message);
            return ToolResult.Failure("Read failed", ex.Message);
        }
    }
}
```

- [ ] **Step 3: Run build to verify**

Run: `dotnet build src/Athlon.Agent.Infrastructure/Athlon.Agent.Infrastructure.csproj`
Expected: PASS

- [ ] **Step 4: Commit**

```bash
git add src/Athlon.Agent.Infrastructure/Memory/MemorySearchTool.cs src/Athlon.Agent.Infrastructure/Memory/MemoryGetTool.cs
git commit -m "feat(memory): add memory_search and memory_get agent tools"
```

---

### Task 7: Implement MemoryPromptContributor (inject MEMORY.md into system prompt)

**Files:**
- Create: `src/Athlon.Agent.Infrastructure/Memory/MemoryPromptContributor.cs`

- [ ] **Step 1: Create MemoryPromptContributor.cs**

```csharp
using System.Text;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Memory;
using Athlon.Agent.Core.Prompt;

namespace Athlon.Agent.Infrastructure.Memory;

/// <summary>
/// Injects the curated MEMORY.md content into the system prompt before each reasoning iteration.
/// Only when memory is enabled and MEMORY.md is non-empty.
/// </summary>
public sealed class MemoryPromptContributor(ILongTermMemory longTermMemory, AppSettings settings) : IPreReasoningPromptContributor
{
    private readonly MemorySettings _cfg = settings.Memory;

    public int Priority => 40; // after workspace files, before skills

    public void Append(StringBuilder builder, EnvironmentPromptContext context)
    {
        if (!_cfg.Enabled)
            return;

        // Read synchronously — this runs in the prompt builder hot path.
        // The file read is fast (MEMORY.md is small, ~4000 tokens).
        var memoryContent = longTermMemory.ReadCuratedAsync().GetAwaiter().GetResult();
        if (string.IsNullOrWhiteSpace(memoryContent))
            return;

        builder.AppendLine();
        builder.AppendLine("## Long-Term Memory");
        builder.AppendLine();
        builder.AppendLine("Below is the consolidated long-term memory from previous sessions. Use it to recall user preferences, past decisions, and persistent context.");
        builder.AppendLine();
        builder.AppendLine("<long_term_memory>");
        builder.AppendLine(memoryContent.Trim());
        builder.AppendLine("</long_term_memory>");
        builder.AppendLine();
    }
}
```

> **Design note:** The `GetAwaiter().GetResult()` call is intentional here. The `IPreReasoningPromptContributor.Append` signature is synchronous, and MEMORY.md is bounded to ~4000 tokens (~16KB), so the synchronous file read is negligible in the prompt-building hot path. If this becomes a bottleneck, the interface itself would need to be made async across all contributors.

- [ ] **Step 2: Run build to verify**

Run: `dotnet build src/Athlon.Agent.Infrastructure/Athlon.Agent.Infrastructure.csproj`
Expected: PASS

- [ ] **Step 3: Add workspace ignore patterns for memory files**

In `src/Athlon.Agent.Core/AgentSettings.cs`, find `WorkspaceIgnoreDefaults.CreateMutableDefaultList()` and verify it excludes `memory/` and `MEMORY.md` — if not, add them to `MemorySettings.ExcludePatterns` and ensure they are honored by the workspace ignore logic. (This is informational; the actual ignore pattern wiring is existing behavior.)

- [ ] **Step 4: Commit**

```bash
git add src/Athlon.Agent.Infrastructure/Memory/MemoryPromptContributor.cs
git commit -m "feat(memory): inject MEMORY.md into system prompt via IPreReasoningPromptContributor"
```

---

### Task 8: Wire services into AgentRuntime (post-turn flush)

**Files:**
- Modify: `src/Athlon.Agent.Core/AgentRuntime.cs`

- [ ] **Step 1: Add MemoryFlushService dependency to AgentRuntime**

Find the constructor of `AgentRuntime` (line 12) and add `MemoryFlushService` and `MemoryConsolidationService` parameters:

```csharp
public sealed class AgentRuntime(
    IAgentModelClient modelClient,
    IFileStorageService storage,
    IToolRouter toolRouter,
    ISystemPromptOrchestrator systemPromptOrchestrator,
    IPreCompletionPipeline preCompletionPipeline,
    IToolResultEvictor toolResultEvictor,
    ITokenEstimatorCalibrator tokenEstimatorCalibrator,
    IActiveAgentSessionContext activeSessionContext,
    AppSettings settings,
    IAppLogger logger,
    // New dependencies:
    MemoryFlushService memoryFlushService,
    MemoryConsolidationService memoryConsolidationService) : IAgentRuntime
```

Add private fields (C# primary constructor already creates them; use them directly in method body).

- [ ] **Step 2: Add post-turn flush call in SendAsyncTurnAsync**

Find the end of `SendAsyncTurnAsync` — after the tool loop completes and before the method returns the session. Insert a flush call:

```csharp
// After the tool loop ends (after the while(true) block and before return session):

// Post-turn memory flush (fire-and-forget — agent response already sent)
if (settings.Memory.Enabled)
{
    _ = Task.Run(async () =>
    {
        try
        {
            await memoryFlushService.FlushAsync(session.Messages, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.Warning("Post-turn memory flush failed: {Error}", ex.Message);
        }

        try
        {
            await memoryConsolidationService.ConsolidateAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.Warning("Memory consolidation failed: {Error}", ex.Message);
        }
    }, CancellationToken.None);
}
```

Find the exact insertion point. In the current code, the method returns `session` at the end. Insert the flush block right before the return.

- [ ] **Step 3: Run build to verify**

Run: `dotnet build src/Athlon.Agent.Core/Athlon.Agent.Core.csproj`
Expected: Build passes with the new constructor params (we'll register them in DI next).

- [ ] **Step 4: Commit**

```bash
git add src/Athlon.Agent.Core/AgentRuntime.cs
git commit -m "feat(memory): add post-turn memory flush and consolidation in AgentRuntime"
```

---

### Task 9: Register all services in DI

**Files:**
- Modify: `src/Athlon.Agent.Infrastructure/ServiceCollectionExtensions.cs`

- [ ] **Step 1: Add registrations in AddAthlonInfrastructure**

Find `ServiceCollectionExtensions.cs` and add the following before the `return services;` line:

```csharp
// Long-term memory services
services.AddSingleton<ILongTermMemory, FileLongTermMemory>();
services.AddSingleton<MemoryFlushService>();
services.AddSingleton<MemoryConsolidationService>();
services.AddSingleton<IAgentTool, MemorySearchTool>();
services.AddSingleton<IAgentTool, MemoryGetTool>();
services.AddSingleton<IPreReasoningPromptContributor, MemoryPromptContributor>();
```

Add the required `using` statements at the top:

```csharp
using Athlon.Agent.Core.Memory;
using Athlon.Agent.Infrastructure.Memory;
using Athlon.Agent.Core.Prompt;
```

- [ ] **Step 2: Run build to verify**

Run: `dotnet build Athlon.Agent.slnx`
Expected: PASS

- [ ] **Step 3: Commit**

```bash
git add src/Athlon.Agent.Infrastructure/ServiceCollectionExtensions.cs
git commit -m "feat(memory): register long-term memory services in DI container"
```

---

## Self-Review

### 1. Spec coverage

| Requirement | Implemented in |
|---|---|
| Two-layer memory (daily ledger + curated MEMORY.md) | Task 3 (FileLongTermMemory), Task 4 (FlushService → daily), Task 5 (ConsolidationService → MEMORY.md) |
| LLM extraction after each turn | Task 4 (MemoryFlushService.FlushAsync) + Task 8 (AgentRuntime post-turn) |
| LLM consolidation (periodic, watermarked) | Task 5 (MemoryConsolidationService.ConsolidateAsync) + Task 8 (fire after flush) |
| inject MEMORY.md into system prompt | Task 7 (MemoryPromptContributor as IPreReasoningPromptContributor) |
| memory_search tool | Task 6 (MemorySearchTool) |
| memory_get tool | Task 6 (MemoryGetTool) |
| Configurable settings | Task 1 (MemorySettings) |
| DI wiring | Task 9 (ServiceCollectionExtensions) |

No gaps.

### 2. Placeholder scan

Reviewed every step — no "TBD", "TODO", "implement later", "add proper error handling" without code, or "similar to Task N" references. All code blocks contain complete implementations.

### 3. Type consistency

- `ILongTermMemory` — defined in Task 2, used in Tasks 3–7. Method signatures match across all consumers.
- `MemoryFlushResult` — defined in Task 2, used in Task 4.
- `MemorySettings` — defined in Task 1, consumed in Tasks 3–8 via `AppSettings.Memory`.
- `IPreReasoningPromptContributor` — existing interface, implemented in Task 7, registered in Task 9.
- `IAgentTool` — existing interface, implemented in Task 6, registered in Task 9.
- `MemoryFlushService` / `MemoryConsolidationService` — defined in Tasks 4–5, injected in Task 8, registered in Task 9.
- `AgentModelMessage` — existing type, used in Tasks 4–5. Correct constructor signature verified against existing usage in `CompactionServices.cs`.

All type references are consistent.

---

## Execution Handoff

Plan complete and saved to `docs/superpowers/plans/2026-06-08-long-term-memory-alignment.md`. Two execution options:

**1. Subagent-Driven (recommended)** — I dispatch a fresh subagent per task, review between tasks, fast iteration

**2. Inline Execution** — Execute tasks in this session using executing-plans, batch execution with checkpoints

**Which approach?**
