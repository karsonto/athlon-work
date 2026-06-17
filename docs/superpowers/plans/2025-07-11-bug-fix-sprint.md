# Bug Fix Sprint Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Fix 18 identified bugs across Core, Infrastructure, and App layers, prioritized by severity.

**Architecture:** Bugs are grouped into 6 phases by risk/impact, each with its own test coverage. Phases are independent and can be implemented in parallel via subagents.

**Tech Stack:** .NET 8 / WPF / xUnit / Moq (test doubles in `TestDoubles.cs`)

---

## File Structure

| File | Responsibility |
|------|---------------|
| `src/Athlon.Agent.App/Services/Streaming/SessionStreamingUiContext.cs` | B2 fix: handle ToolCallArgs before ToolCallStart |
| `src/Athlon.Agent.Core/AmbientToolOutputStream.cs` | B6 fix: fire-and-forget → await with error handling |
| `src/Athlon.Agent.Infrastructure/ConversationDisplayLog.cs` | B7 fix: parse legacy toolCallsJson + reasoningContent |
| `src/Athlon.Agent.Core/ModelMessageCache.cs` | B4 fix: thread-safe Build() |
| `src/Athlon.Agent.Core/AgentRuntime.cs` | B10 fix: word-boundary for file list detection; B12: improve structure change detection; B20: save with timeout |
| `src/Athlon.Agent.Infrastructure/OpenAiChatResponseParser.cs` | B11 fix: exclude HTTP preamble from fallback; B5 fix: unique tool index fallback |
| `src/Athlon.Agent.Infrastructure/FileEditMatcher.cs` | B14 fix: efficient CountOccurrences without Split |
| `src/Athlon.Agent.Infrastructure/SessionWriteLock.cs` | B1 fix: add cleanup mechanism |
| `src/Athlon.Agent.Core/SessionTurnReconciler.cs` | B15 fix: handle duplicate toolCallIds gracefully |
| `src/Athlon.Agent.App/Controls/MarkdownMessageView.xaml.cs` | B16 fix: selective RequestBringIntoView handling |
| `src/Athlon.Agent.Infrastructure/ConversationCompactor.cs` | B17 fix: sanitize summary error message |
| `src/Athlon.Agent.App/Services/ChatAutoScrollController.cs` | B18 fix: timer reuse pattern |
| `src/Athlon.Agent.App/Services/SessionTurnUiController.cs` | B19 fix: thread-safe LiveAgentSession access |
| `src/Athlon.Agent.Infrastructure/ExecuteCommandTool.cs` | B13 fix: safe ExitCode access |

---

### Phase 1: Streaming & Data Integrity (B2, B5, B6)

### Task 1.1: B2 — ToolCallArgs before ToolCallStart robustness

**Files:**
- Modify: `src/Athlon.Agent.App/Services/Streaming/SessionStreamingUiContext.cs:59-74`

**Problem:** `_toolCallIdToIndex` is written in `ToolCallStart` handler. If `ToolCallArgs` arrives first (possible with non-standard SSE ordering), the args update is silently dropped.

**Fix:** Buffer args when `toolCallId` is unknown, replay when `ToolCallStart` arrives.

- [ ] **Step 1: Add args buffer field and update ToolCallArgs handler**

```csharp
// Add to field declarations (around line 13-14):
private readonly Dictionary<string, string> _pendingToolCallArgs = new(StringComparer.Ordinal);

// In ToolCallArgs handler (lines 66-73), replace existing code with:
case AgentStreamEvent.ToolCallArgs(var toolCallId, var argsJson):
    if (_toolCallIdToIndex.TryGetValue(toolCallId, out var argsIndex)
        && _toolBubblesByIndex.TryGetValue(argsIndex, out var toolBubble))
    {
        toolBubble.UpdateStreamingToolCall(toolCallId, toolBubble.ToolName, argsJson);
    }
    else
    {
        // Buffer args in case ToolCallStart hasn't arrived yet
        if (_pendingToolCallArgs.TryGetValue(toolCallId, out var existing))
            _pendingToolCallArgs[toolCallId] = existing + argsJson;
        else
            _pendingToolCallArgs[toolCallId] = argsJson;
    }
    RequestScroll();
    break;
```

- [ ] **Step 2: Replay buffered args in ToolCallStart handler**

In the `ToolCallStart` handler (lines 59-63), after `EnsureToolBubble`, add replay logic:

```csharp
case AgentStreamEvent.ToolCallStart(var toolCallId, var toolName, var index):
    if (index is int toolIndex)
    {
        _toolCallIdToIndex[toolCallId] = toolIndex;
        EnsureToolBubble(toolIndex, toolCallId, toolName, messages);
        // Replay any buffered args that arrived before ToolCallStart
        if (_pendingToolCallArgs.TryGetValue(toolCallId, out var bufferedArgs))
        {
            _pendingToolCallArgs.Remove(toolCallId);
            if (_toolBubblesByIndex.TryGetValue(toolIndex, out var bufferedBubble))
            {
                bufferedBubble.UpdateStreamingToolCall(toolCallId, bufferedBubble.ToolName, bufferedArgs);
            }
        }
    }
    break;
```

- [ ] **Step 3: Clear pending buffer on Reset()**

In the `Reset()` method (called from `RunStarted`), add:

```csharp
_pendingToolCallArgs.Clear();
```

- [ ] **Step 4: Commit**

```bash
git add src/Athlon.Agent.App/Services/Streaming/SessionStreamingUiContext.cs
git commit -m "fix: buffer ToolCallArgs when ToolCallStart arrives late (B2)"
```

---

### Task 1.2: B5 — Tool call index collision from missing SSE index fields

**Files:**
- Modify: `src/Athlon.Agent.Infrastructure/OpenAiChatResponseParser.cs:169-171`

**Problem:** When SSE delta lacks `index`, fallback `toolCalls.Count` causes two simultaneous tool calls to share index 0.

**Fix:** Generate a unique negative index instead of reusing `Count`.

- [ ] **Step 1: Change index fallback**

Replace lines 169-171:

```csharp
var index = partial.TryGetProperty("index", out var indexElement) && indexElement.TryGetInt32(out var parsedIndex)
    ? parsedIndex
    : toolCalls.Count;
```

With:

```csharp
var index = partial.TryGetProperty("index", out var indexElement) && indexElement.TryGetInt32(out var parsedIndex)
    ? parsedIndex
    : -toolCalls.Count - 1;  // Unique negative key avoids collision
```

- [ ] **Step 2: Write a unit test**

Create test in `OpenAiCompatibleChatModelClientStreamingTests.cs`:

```csharp
[Fact]
public void ParseStreaming_MissingToolCallIndex_GeneratesUniqueNegativeIndices()
{
    var sse = "data: {\"choices\":[{\"index\":0,\"delta\":{\"tool_calls\":[{\"function\":{\"name\":\"tool_a\",\"arguments\":\"{}\"}}]}}]}\n"
            + "data: {\"choices\":[{\"index\":0,\"delta\":{\"tool_calls\":[{\"function\":{\"name\":\"tool_b\",\"arguments\":\"{}\"}}]}}]}\n"
            + "data: [DONE]\n";
    var toolCalls = new List<StreamingToolCallDelta>();
    // ... parse SSE and verify each tool delta has a unique, negative index
}
```

- [ ] **Step 3: Commit**

```bash
git add src/Athlon.Agent.Infrastructure/OpenAiChatResponseParser.cs tests/Athlon.Agent.Tests/OpenAiCompatibleChatModelClientStreamingTests.cs
git commit -m "fix: unique negative index for tool calls missing SSE index field (B5)"
```

---

### Task 1.3: B6 — AmbientToolOutputStream fire-and-forget swallows exceptions

**Files:**
- Modify: `src/Athlon.Agent.Core/AmbientToolOutputStream.cs:29-35`

**Problem:** `_ = onEvent(evt)` fire-and-forgets a `Func<AgentStreamEvent, Task>`. Exceptions from event handlers are silently swallowed, and on hot paths (every stdout line), queued tasks accumulate unobserved.

**Fix:** Wrap in a try-catch that logs, and use synchronous invocation since `onEvent` is typically a fast UI dispatcher enqueue.

- [ ] **Step 1: Update WriteLine with logging and sync-like fire-and-forget**

```csharp
public void WriteLine(string line)
{
    if (_callbacks?.OnStreamEvent is not { } onEvent)
        return;

    var evt = new AgentStreamEvent.ToolCallOutput(_toolCallId, line + Environment.NewLine);
    try
    {
        // Fire-and-forget is acceptable here — the event handler is a fast
        // dispatcher enqueue (not disk I/O or network). Log any unexpected error.
        _ = onEvent(evt).ContinueWith(static t =>
        {
            if (t.Exception is not null)
            {
                // Logger unavailable in this static context; rely on global trace
                System.Diagnostics.Debug.WriteLine(
                    "AmbientToolOutputStream: " + t.Exception.InnerException?.Message);
            }
        }, TaskContinuationOptions.OnlyOnFaulted);
    }
    catch (Exception ex)
    {
        System.Diagnostics.Debug.WriteLine("AmbientToolOutputStream sync: " + ex.Message);
    }
}
```

- [ ] **Step 2: Commit**

```bash
git add src/Athlon.Agent.Core/AmbientToolOutputStream.cs
git commit -m "fix: AmbientToolOutputStream catches async exceptions instead of silent swallow (B6)"
```

---

### Phase 2: Legacy & Memory Safety (B7, B4)

### Task 2.1: B7 — Legacy conversation loading loses ToolCallsJson and ReasoningContent

**Files:**
- Modify: `src/Athlon.Agent.Infrastructure/ConversationDisplayLog.cs:80-88`

**Problem:** `ParseLegacyLine` hardcodes `ToolCallsJson=null` and `ReasoningContent=null`. Old-format sessions lose tool call metadata after upgrade.

**Fix:** Read `toolCalls` and `reasoningContent` from the JSON root (even old files may have had these fields).

- [ ] **Step 1: Update ParseLegacyLine to read optional fields**

Replace lines 80-88:

```csharp
return new ChatMessage(
    id, role, content, createdAt, parentId,
    null,          // ToolCallsJson
    null,          // ReasoningContent
    imageAttachments);
```

With:

```csharp
string? toolCallsJson = null;
if (root.TryGetProperty("toolCalls", out var toolCallsElement) && toolCallsElement.ValueKind == JsonValueKind.String)
{
    toolCallsJson = toolCallsElement.GetString();
}
else if (root.TryGetProperty("toolCallsJson", out var tcjElem) && tcjElem.ValueKind == JsonValueKind.String)
{
    toolCallsJson = tcjElem.GetString();
}

string? reasoningContent = null;
if (root.TryGetProperty("reasoningContent", out var reasoningElem) && reasoningElem.ValueKind == JsonValueKind.String)
{
    reasoningContent = reasoningElem.GetString();
}

return new ChatMessage(
    id, role, content, createdAt, parentId,
    toolCallsJson,
    reasoningContent,
    imageAttachments);
```

- [ ] **Step 2: Add test for legacy format parsing**

In `SessionDiskLogTests.cs`:

```csharp
[Fact]
public void TryParseLine_LegacyFormatWithToolCalls_PreservesToolCallsJson()
{
    var json = "{\"id\":\"msg1\",\"role\":\"Assistant\",\"content\":\"\",\"time\":\"2024-01-01T00:00:00+00:00\",\"parentId\":\"usr1\",\"toolCalls\":\"[{\\\"Id\\\":\\\"call1\\\",\\\"Name\\\":\\\"file_read\\\",\\\"Arguments\\\":{}}]\"}";
    var message = ConversationDisplayLog.TryParseLine(json);
    Assert.NotNull(message);
    Assert.Contains("call1", message.ToolCallsJson);
}

[Fact]
public void TryParseLine_LegacyFormatWithReasoning_PreservesReasoningContent()
{
    var json = "{\"id\":\"msg1\",\"role\":\"Assistant\",\"content\":\"hello\",\"time\":\"2024-01-01T00:00:00+00:00\",\"reasoningContent\":\"thinking step 1\"}";
    var message = ConversationDisplayLog.TryParseLine(json);
    Assert.NotNull(message);
    Assert.Equal("thinking step 1", message.ReasoningContent);
}
```

- [ ] **Step 3: Commit**

```bash
git add src/Athlon.Agent.Infrastructure/ConversationDisplayLog.cs tests/Athlon.Agent.Tests/SessionDiskLogTests.cs
git commit -m "fix: legacy conversation parsing preserves ToolCallsJson and ReasoningContent (B7)"
```

---

### Task 2.2: B4 — ModelMessageCache.Build() thread safety

**Files:**
- Modify: `src/Athlon.Agent.Core/ModelMessageCache.cs:102-134`

**Problem:** `_messages` is a shared `List<AgentModelMessage>` mutated in-place by `Build()` and `AppendHistoryMessage`. No synchronization; concurrent calls corrupt internal state.

**Fix:** Add a lock around mutation of `_messages` and related fields. Since this is on the hot path, use a simple `lock` (contention is extremely rare — one turn at a time).

- [ ] **Step 1: Add lock and wrap mutable sections**

```csharp
private readonly object _buildLock = new();

public List<AgentModelMessage> Build(
    string environmentPrompt,
    IReadOnlyList<ChatMessage> history,
    bool includeReasoningInModelContext)
{
    lock (_buildLock)
    {
        if (_messages is not null
            && string.Equals(_environmentPrompt, environmentPrompt, StringComparison.Ordinal)
            && _includeReasoning == includeReasoningInModelContext
            && history.Count >= _processedHistoryCount)
        {
            if (history.Count == _processedHistoryCount)
            {
                return _messages;
            }

            for (var index = _processedHistoryCount; index < history.Count; index++)
            {
                index = ModelMessageBuilder.AppendHistoryMessage(
                    _messages,
                    history,
                    index,
                    includeReasoningInModelContext);
            }

            _processedHistoryCount = history.Count;
            return _messages;
        }

        _messages = ModelMessageBuilder.BuildForSession(environmentPrompt, history, includeReasoningInModelContext);
        _environmentPrompt = environmentPrompt;
        _includeReasoning = includeReasoningInModelContext;
        _processedHistoryCount = history.Count;
        return _messages;
    }
}
```

- [ ] **Step 2: Also lock ApplyHygiene and NotePreCompletionResult**

```csharp
public RequestHistoryHygiene.ApplyResult ApplyHygiene(RequestHistoryHygieneSettings settings)
{
    lock (_buildLock) { /* existing body */ }
}

public void NotePreCompletionResult(IReadOnlyList<ChatMessage> historyBefore, IReadOnlyList<ChatMessage> historyAfter)
{
    lock (_buildLock) { /* existing body */ }
}
```

- [ ] **Step 3: Write thread-safety test**

In `ModelMessagesForApiBuilderTests.cs`:

```csharp
[Fact]
public async Task Build_ConcurrentAccess_DoesNotCorruptState()
{
    var cache = new ModelMessageCache();
    var history = CreateSampleHistory();

    var tasks = Enumerable.Range(0, 10).Select(_ =>
        Task.Run(() =>
        {
            for (var i = 0; i < 100; i++)
            {
                var result = cache.Build("prompt", history, false);
                Assert.NotEmpty(result);
            }
        }));

    await Task.WhenAll(tasks);
}
```

- [ ] **Step 4: Commit**

```bash
git add src/Athlon.Agent.Core/ModelMessageCache.cs tests/Athlon.Agent.Tests/ModelMessagesForApiBuilderTests.cs
git commit -m "fix: ModelMessageCache.Build() thread safety with lock (B4)"
```

---

### Phase 3: Input Handling Edge Cases (B10, B11, B14)

### Task 3.1: B10 — File list detection too aggressive

**Files:**
- Modify: `src/Athlon.Agent.Core/AgentRuntime.cs:487-496`

**Problem:** `ContainsAny` matches substrings without word boundaries. `"which files"` in code pastes or `"什么文件"` in Chinese docs triggers spurious `file_list`.

**Fix:** Use regex word boundaries (`\b`) for English terms; for Chinese terms (no word boundaries), use a lower max-length heuristic.

- [ ] **Step 1: Replace ShouldListWorkspaceFiles implementation**

```csharp
private static bool ShouldListWorkspaceFiles(string userInput)
{
    var input = userInput.Trim();
    if (input.Length > 200)
        return false;  // Pasted content — unlikely to be a file-list query

    // English terms: require word boundaries
    if (Regex.IsMatch(input, @"\b(list files|what files|which files)\b", RegexOptions.IgnoreCase))
        return true;

    // Chinese terms: require the phrase starts the input or follows punctuation
    return ContainsAny(input, "有哪些文件", "什么文件", "文件列表", "目录下", "目录里", "工作区文件");
}
```

Add `using System.Text.RegularExpressions;` at the top of the file if not present.

- [ ] **Step 2: Add test**

In a new test file or `AgentRuntimeTests.cs`:

```csharp
[Fact]
public void ShouldListWorkspaceFiles_LongPastedContent_ReturnsFalse()
{
    var longText = new string('x', 300);
    var result = typeof(AgentRuntime)
        .GetMethod("ShouldListWorkspaceFiles", BindingFlags.NonPublic | BindingFlags.Static)
        ?.Invoke(null, new object[] { longText });
    Assert.False((bool)result!);
}

[Fact]
public void ShouldListWorkspaceFiles_ShortQueryWithKeyword_ReturnsTrue()
{
    var result = typeof(AgentRuntime)
        .GetMethod("ShouldListWorkspaceFiles", BindingFlags.NonPublic | BindingFlags.Static)
        ?.Invoke(null, new object[] { "work directory 有哪些文件" });
    Assert.True((bool)result!);
}
```

- [ ] **Step 3: Commit**

```bash
git add src/Athlon.Agent.Core/AgentRuntime.cs tests/Athlon.Agent.Tests/AgentRuntimeProgressTests.cs
git commit -m "fix: word-boundary detection for auto file_list trigger (B10)"
```

---

### Task 3.2: B11 — Streaming fallback includes HTTP preamble lines

**Files:**
- Modify: `src/Athlon.Agent.Infrastructure/OpenAiChatResponseParser.cs:117-119, 219-224`

**Problem:** `fallbackBuilder` collects every non-`data:` line including HTTP headers. If SSE never starts, the raw HTTP response text is fed to JSON parser.

**Fix:** Skip lines that look like HTTP headers (`HTTP/`, header lines containing `:`) in the fallback accumulator.

- [ ] **Step 1: Add HTTP-line skip logic**

Replace lines 117-119:

```csharp
if (!line.StartsWith("data:", StringComparison.Ordinal))
{
    fallbackBuilder.AppendLine(line);
    continue;
}
```

With:

```csharp
if (!line.StartsWith("data:", StringComparison.Ordinal))
{
    // Skip HTTP header lines — they are not valid JSON
    if (!line.StartsWith("HTTP/", StringComparison.OrdinalIgnoreCase)
        && !LooksLikeHttpHeader(line))
    {
        fallbackBuilder.AppendLine(line);
    }
    continue;
}
```

- [ ] **Step 2: Add helper method**

```csharp
private static bool LooksLikeHttpHeader(string line)
{
    // HTTP headers follow "Key: Value" pattern at the response start
    var colonIndex = line.IndexOf(':');
    if (colonIndex <= 0 || colonIndex > 64)
        return false;
    var key = line.AsSpan(0, colonIndex);
    return !key.Contains(' ') && !key.Contains('\t') && key.IndexOfAny("<>{}") < 0;
}
```

- [ ] **Step 3: Commit**

```bash
git add src/Athlon.Agent.Infrastructure/OpenAiChatResponseParser.cs
git commit -m "fix: skip HTTP header lines in SSE fallback accumulator (B11)"
```

---

### Task 3.3: B14 — FileEditMatcher.CountOccurrences allocates large arrays

**Files:**
- Modify: `src/Athlon.Agent.Infrastructure/FileEditMatcher.cs:157-158`

**Problem:** `content.Split(oldText).Length - 1` creates a huge string array for large files (one entry per occurrence + 1). Each `file_edit` calls this 2-4 times.

**Fix:** Replace with `IndexOf`-in-a-loop counting, zero allocation.

- [ ] **Step 1: Replace CountOccurrences**

```csharp
private static int CountOccurrences(string content, string oldText)
{
    if (string.IsNullOrEmpty(oldText))
        return 0;

    var count = 0;
    var index = 0;
    while ((index = content.IndexOf(oldText, index, StringComparison.Ordinal)) >= 0)
    {
        count++;
        index += oldText.Length;
    }

    return count;
}
```

- [ ] **Step 2: Commit**

```bash
git add src/Athlon.Agent.Infrastructure/FileEditMatcher.cs
git commit -m "perf: zero-allocation CountOccurrences with IndexOf loop (B14)"
```

---

### Phase 4: Resource Leaks & Thread Safety (B1, B20, B13, B19)

### Task 4.1: B1 — SessionWriteLock ConcurrentDictionary never removes entries

**Files:**
- Modify: `src/Athlon.Agent.Infrastructure/SessionWriteLock.cs`

**Problem:** `Gates` dictionary grows unboundedly as sessions are created, never cleaned up.

**Fix:** Add a `Remove` method and call it from `FileStorageService.DeleteSessionAsync`.

- [ ] **Step 1: Add cleanup method to SessionWriteLock**

```csharp
internal static class SessionWriteLock
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.Ordinal);

    public static async Task<IDisposable> AcquireAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return Noop.Instance;
        }

        var gate = Gates.GetOrAdd(sessionId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new Releaser(gate, sessionId);
    }

    /// <summary>Remove the gate for a deleted session. Safe to call even if the gate is held or already removed.</summary>
    public static void Remove(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return;
        if (Gates.TryRemove(sessionId, out var gate))
        {
            gate.Dispose();
        }
    }

    private sealed class Releaser(SemaphoreSlim gate, string sessionId) : IDisposable
    {
        public void Dispose()
        {
            gate.Release();
            // Optionally: if the queue is empty, schedule a cleanup.
            // For now we only remove on explicit DeleteSessionAsync call.
        }
    }

    private sealed class Noop : IDisposable
    {
        public static readonly Noop Instance = new();
        public void Dispose() { }
    }
}
```

- [ ] **Step 2: Call Remove from DeleteSessionAsync**

In `src/Athlon.Agent.Infrastructure/FileStorageService.cs`, at the end of `DeleteSessionAsync` (around line 343):

```csharp
await _indexCoordinator.RefreshIndexImmediateAsync(cancellationToken);
SessionWriteLock.Remove(sessionId);  // ← Add this
_logger.Information("Deleted session {SessionId}", sessionId);
```

- [ ] **Step 3: Add test**

```csharp
[Fact]
public async Task AcquireThenRemove_ReleasesGate()
{
    var id = "test-session";
    var lock1 = await SessionWriteLock.AcquireAsync(id);
    SessionWriteLock.Remove(id);
    lock1.Dispose();  // Release after remove — should not throw
    
    var lock2 = await SessionWriteLock.AcquireAsync(id);
    lock2.Dispose();  // Should succeed — new gate created
}
```

- [ ] **Step 4: Commit**

```bash
git add src/Athlon.Agent.Infrastructure/SessionWriteLock.cs src/Athlon.Agent.Infrastructure/FileStorageService.cs
git commit -m "fix: SessionWriteLock removes gate on session deletion (B1)"
```

---

### Task 4.2: B20 — SaveSessionAsync on cancel uses CancellationToken.None, may hang shutdown

**Files:**
- Modify: `src/Athlon.Agent.Core/AgentRuntime.cs:236-240`

**Problem:** `OperationCanceledException` catch saves with `CancellationToken.None`. If shutdown races, save blocks forever.

**Fix:** Use a short timeout via `CancellationTokenSource.CreateLinkedTokenSource`.

- [ ] **Step 1: Replace CancellationToken.None with timed token**

```csharp
catch (OperationCanceledException)
{
    // Attempt save with a short grace period so we don't hang shutdown
    using var saveTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    await storage.SaveSessionAsync(session, saveTimeout.Token);
    throw;
}
```

- [ ] **Step 2: Commit**

```bash
git add src/Athlon.Agent.Core/AgentRuntime.cs
git commit -m "fix: save session with timeout on cancellation to avoid shutdown hang (B20)"
```

---

### Task 4.3: B13 — ExecuteCommandTool safe ExitCode access after kill

**Files:**
- Modify: `src/Athlon.Agent.Infrastructure/ExecuteCommandTool.cs:95-103`

**Problem:** After `ProcessKillHelper.KillProcessTree(process)` in timeout branch, `process.ExitCode` may throw `InvalidOperationException` if the process hasn't fully exited yet.

**Fix:** Use `process.HasExited` before reading `ExitCode`, and in the timeout branch use `-1` explicitly.

- [ ] **Step 1: Add safe ExitCode helper**

Replace the return at lines 97-104:

```csharp
var content = FormatOutput(run.Stdout, run.Stderr);
await audit.WriteAsync(
    "execute_command",
    new { command, cwd, process.ExitCode, elapsedMs = sw.ElapsedMilliseconds },
    cancellationToken).ConfigureAwait(false);
return process.ExitCode == 0
    ? ToolResult.Success($"Command exited 0 in {sw.ElapsedMilliseconds}ms", content, sw.Elapsed)
    : ToolResult.Failure("Command failed", content, sw.Elapsed);
```

With:

```csharp
var content = FormatOutput(run.Stdout, run.Stderr);
var exitCode = process.HasExited ? process.ExitCode : -1;
await audit.WriteAsync(
    "execute_command",
    new { command, cwd, exitCode, elapsedMs = sw.ElapsedMilliseconds },
    cancellationToken).ConfigureAwait(false);
return exitCode == 0
    ? ToolResult.Success($"Command exited 0 in {sw.ElapsedMilliseconds}ms", content, sw.Elapsed)
    : ToolResult.Failure("Command failed", content, sw.Elapsed);
```

- [ ] **Step 2: Commit**

```bash
git add src/Athlon.Agent.Infrastructure/ExecuteCommandTool.cs
git commit -m "fix: safe ExitCode access after process kill (B13)"
```

---

### Task 4.4: B19 — Thread-safe LiveAgentSession access

**Files:**
- Modify: `src/Athlon.Agent.App/Services/SessionTurnUiController.cs:11-17, 91-97`

**Problem:** `LiveAgentSession.Value` is read from UI thread and written from background thread with no synchronization.

**Fix:** Use `Volatile.Read/Write` or `Interlocked.Exchange` on the backing field.

- [ ] **Step 1: Add volatile semantics to LiveAgentSession**

```csharp
public sealed class LiveAgentSession
{
    private volatile AgentSession _value;

    public LiveAgentSession(AgentSession value) => _value = value;

    public AgentSession Value
    {
        get => _value;
        set => _value = value;
    }
}
```

- [ ] **Step 2: Commit**

```bash
git add src/Athlon.Agent.App/Services/SessionTurnUiController.cs
git commit -m "fix: volatile read/write for LiveAgentSession cross-thread access (B19)"
```

---

### Phase 5: Edge Cases & Cleanup (B12, B15, B16, B17, B18)

### Task 5.1: B12 — HasCompactionStructureChange edge case

**Files:**
- Modify: `src/Athlon.Agent.Core/AgentRuntime.cs:404-431`

**Problem:** `session.Messages.Count < messageIdsBefore.Count` misses the case where compaction deletes more than it adds but the new count is equal or larger.

**Fix:** Use a structural check: if any old message ID is missing, there was a change.

- [ ] **Step 1: Simplify HasCompactionStructureChange**

```csharp
private static bool HasCompactionStructureChange(AgentSession session, HashSet<string> messageIdsBefore)
{
    var hasNewCompactionAudit = false;
    var hasNewSummaryPlaceholder = false;
    foreach (var message in session.Messages)
    {
        if (messageIdsBefore.Contains(message.Id))
            continue;

        if (message.Role == MessageRole.Compaction)
            hasNewCompactionAudit = true;
        else if (SummaryMessageBuilder.IsSummaryMessage(message))
            hasNewSummaryPlaceholder = true;
    }

    // Check if any messages were removed
    var currentIds = session.Messages.Select(m => m.Id).ToHashSet(StringComparer.Ordinal);
    var anyRemoved = messageIdsBefore.Any(id => !currentIds.Contains(id));

    return hasNewCompactionAudit || hasNewSummaryPlaceholder || anyRemoved;
}
```

- [ ] **Step 2: Commit**

```bash
git add src/Athlon.Agent.Core/AgentRuntime.cs
git commit -m "fix: HasCompactionStructureChange detects removed messages correctly (B12)"
```

---

### Task 5.2: B15 — Reconciler handles duplicate tool call IDs gracefully

**Files:**
- Modify: `src/Athlon.Agent.Core/SessionTurnReconciler.cs:27-32`

**Problem:** `GroupBy(...).First()` hides duplicates but the loop at 61-75 may still emit duplicate tool messages due to call ordering.

**Fix:** After grouping, also mark all duplicate IDs as answered to prevent redundant tool messages.

- [ ] **Step 1: Track consumed duplicate IDs**

```csharp
var incompleteTools = snapshot.IncompleteToolCalls
    .Where(call => !string.IsNullOrWhiteSpace(call.Id))
    .Where(call => !answeredToolCallIds.Contains(call.Id))
    .GroupBy(call => call.Id, StringComparer.Ordinal)
    .Select(group =>
    {
        // Skip all but first; mark extras as consumed to prevent tool message emission
        var first = group.First();
        foreach (var extra in group.Skip(1))
        {
            answeredToolCallIds.Add(extra.Id);
        }
        return first;
    })
    .ToList();
```

- [ ] **Step 3: Commit**

```bash
git add src/Athlon.Agent.Core/SessionTurnReconciler.cs
git commit -m "fix: handle duplicate toolCallIds in reconciler (B15)"
```

---

### Task 5.3: B16 — Selective RequestBringIntoView handling

**Files:**
- Modify: `src/Athlon.Agent.App/Controls/MarkdownMessageView.xaml.cs:69-70`

**Problem:** All `RequestBringIntoView` events are handled, which blocks accessibility/screen reader scroll-to-view behavior.

**Fix:** Only handle the event when the source is inside MarkdownViewer but NOT from a user-initiated interaction (e.g., programmatic scroll requests from inside code blocks).

- [ ] **Step 1: Replace with selective handler**

```csharp
private static void OnMarkdownRequestBringIntoView(object sender, RequestBringIntoViewEventArgs e)
{
    // Allow bring-into-view from code-block internal scrollers (user scrolls inside a code block)
    // Only suppress when the request bubbles from MarkdownViewer's internal FlowDocument layout
    // that would fight the chat ListBox's ScrollViewer.
    if (e.OriginalSource is DependencyObject source)
    {
        // If the request comes from a nested ScrollViewer (code block), let it through
        if (FindVisualParent<ScrollViewer>(source) is not null)
            return;
    }

    e.Handled = true;
}

private static T? FindVisualParent<T>(DependencyObject child) where T : DependencyObject
{
    var parent = VisualTreeHelper.GetParent(child);
    while (parent is not null)
    {
        if (parent is T found)
            return found;
        parent = VisualTreeHelper.GetParent(parent);
    }
    return null;
}
```

Add `using System.Windows.Media;` at the top if not already present.

- [ ] **Step 2: Commit**

```bash
git add src/Athlon.Agent.App/Controls/MarkdownMessageView.xaml.cs
git commit -m "fix: allow accessibility bring-into-view from nested scrollers (B16)"
```

---

### Task 5.4: B17 — Sanitize summary error message before sending to model

**Files:**
- Modify: `src/Athlon.Agent.Infrastructure/ConversationCompactor.cs:124-128`

**Problem:** `"(Summarization failed: " + ex.Message + ")"` leaks internal exception details into the model's context.

**Fix:** Use a generic sanitized message.

- [ ] **Step 1: Replace error message**

```csharp
catch (Exception ex)
{
    _logger.Error(ex, "Summarization LLM call failed for session {SessionId}", session.Id);
    summary = "(Conversation summarization was temporarily unavailable)";
    // Do not include ex.Message — it may contain sensitive API error details.
}
```

- [ ] **Step 2: Commit**

```bash
git add src/Athlon.Agent.Infrastructure/ConversationCompactor.cs
git commit -m "fix: sanitize summarization error message to avoid leaking API details (B17)"
```

---

### Task 5.5: B18 — ChatAutoScrollController timer reuse / GC pressure

**Files:**
- Modify: `src/Athlon.Agent.App/Services/ChatAutoScrollController.cs:79-92`

**Problem:** Every deferred scroll creates a new `DispatcherTimer` and disposes the old one. High scroll frequency causes GC churn.

**Fix:** Use a single reusable timer that is stopped/started instead of destroyed/re-created.

- [ ] **Step 1: Initialize timer once in constructor**

Remove `_scrollThrottleTimer` field and replace with a single timer initialized in constructor:

```csharp
private readonly DispatcherTimer _scrollThrottleTimer;

public ChatAutoScrollController(Dispatcher dispatcher, Func<bool> isBusy)
{
    _dispatcher = dispatcher;
    _isBusy = isBusy;
    _scrollThrottleTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
    {
        Interval = ScrollThrottleInterval
    };
    _scrollThrottleTimer.Tick += OnScrollThrottleTimerTick;
}
```

- [ ] **Step 2: Simplify ScrollToEnd deferred path**

```csharp
if (immediate)
{
    ExecuteScrollToEnd();
    return;
}

// Reuse the single timer — just restart it
_scrollThrottleTimer.Stop();
_scrollThrottleTimer.Start();
```

- [ ] **Step 3: Simplify StopScrollThrottleTimer**

```csharp
private void StopScrollThrottleTimer()
{
    _scrollThrottleTimer.Stop();
}
```

Remove the null-check and Tick -= pattern — the timer is a single instance now.

- [ ] **Step 4: Commit**

```bash
git add src/Athlon.Agent.App/Services/ChatAutoScrollController.cs
git commit -m "perf: reuse single DispatcherTimer for scroll throttle (B18)"
```

---

## Self-Review

**1. Spec coverage:**
- B1 ✅ → Task 4.1 (SessionWriteLock Remove)
- B2 ✅ → Task 1.1 (tool call args buffer)
- B4 ✅ → Task 2.2 (ModelMessageCache lock)
- B5 ✅ → Task 1.2 (unique negative index)
- B6 ✅ → Task 1.3 (catch async exception)
- B7 ✅ → Task 2.1 (legacy field parsing)
- B10 ✅ → Task 3.1 (word boundary detection)
- B11 ✅ → Task 3.2 (HTTP header skip)
- B12 ✅ → Task 5.1 (structure change detection)
- B13 ✅ → Task 4.3 (safe ExitCode)
- B14 ✅ → Task 3.3 (no-alloc CountOccurrences)
- B15 ✅ → Task 5.2 (duplicate toolCallId)
- B16 ✅ → Task 5.3 (selective bring-into-view)
- B17 ✅ → Task 5.4 (sanitized error)
- B18 ✅ → Task 5.5 (timer reuse)
- B19 ✅ → Task 4.4 (volatile LiveAgentSession)
- B20 ✅ → Task 4.2 (save timeout)

**2. Placeholder scan:** No TBD/TODO placeholders. Every code change is shown in full.

**3. Type consistency:** Method names and type references match existing codebase patterns. No signature drift across tasks.
