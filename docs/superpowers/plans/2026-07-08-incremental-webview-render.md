# Incremental WebView Rendering Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace full WebView `NavigateToString` on every session switch with incremental JavaScript event replay, eliminating hundreds of milliseconds of blank-screen delay when switching conversations.

**Architecture:** Separate shell loading (CSS, JS libraries, timeline script) from message rendering. Load shell once during WebView initialization; all subsequent `LoadMessagesAsync` calls dispatch a single `ExecuteScriptAsync("replayEvents([...])")` that resets and rebuilds the DOM incrementally via existing `handleEvent` infrastructure.

**Tech Stack:** C# WPF, WebView2, embedded JavaScript timeline, `ExecuteScriptAsync` for JS interop

---

## File Structure

| File | Responsibility |
|------|---------------|
| `src/Athlon.Agent.App/Services/ChatHtmlBuilder.cs` | Add `BuildReplayScript()` method; keep `BuildShellHtml()` for first load |
| `src/Athlon.Agent.App/Services/ChatEventSerializer.cs` | Add `BuildReplayEventsJson()` to return raw JSON array (not base64-encoded) |
| `src/Athlon.Agent.App/Controls/WebChatView.xaml.cs` | Split first-load vs subsequent-load paths in `LoadMessagesAsync` / `EnsureInitializedAndRenderAsync` |

---

### Task 1: Add `BuildReplayEventsJson` to `ChatEventSerializer`

**Files:**
- Modify: `src/Athlon.Agent.App/Services/ChatEventSerializer.cs:101-122`

The existing `SerializeEventsToJsonArray` takes pre-serialized JSON strings and concatenates them. We need a direct path that returns the raw JSON array string (no base64), so we can embed it in a JS call.

- [ ] **Step 1: Add `BuildReplayEventsJson` method**

Add after `SerializeEventsToJsonArray`:

```csharp
public static string BuildReplayEventsJson(
    IReadOnlyList<ChatMessageViewModel> messages,
    bool showToolCalls = false)
{
    var events = BuildReplayEvents(messages, showToolCalls);
    return SerializeEventsToJsonArray(events);
}
```

This is a simple delegating method — `BuildReplayEvents` already returns `List<string>` of serialized event JSONs, and `SerializeEventsToJsonArray` already builds `[event1, event2, ...]`.

- [ ] **Step 2: Commit**

```bash
git add src/Athlon.Agent.App/Services/ChatEventSerializer.cs
git commit -m "refactor: expose BuildReplayEventsJson for incremental render usage"
```

---

### Task 2: Add `BuildReplayScript` to `ChatHtmlBuilder`

**Files:**
- Modify: `src/Athlon.Agent.App/Services/ChatHtmlBuilder.cs:52-83`

- [ ] **Step 1: Add `BuildReplayScript` method after `BuildDocumentHtml`**

```csharp
/// <summary>
/// Generates a JavaScript snippet that resets the timeline and replays all messages
/// via the existing replayEvents() function. This is used for incremental rendering
/// after the shell page has been loaded once — no full WebView navigation needed.
/// </summary>
public string BuildReplayScript(
    IReadOnlyList<ChatMessageViewModel> messages,
    bool showToolCalls = false)
{
    var eventsJson = ChatEventSerializer.BuildReplayEventsJson(messages, showToolCalls);
    return $"replayEvents({eventsJson});";
}
```

- [ ] **Step 2: Verify `BuildDocumentHtml` still works for first-load path**

`BuildDocumentHtml` embeds the same events as a base64 payload and calls `replayEvents` after page load. Keep it unchanged — it's still used for the very first WebView navigation.

- [ ] **Step 3: Commit**

```bash
git add src/Athlon.Agent.App/Services/ChatHtmlBuilder.cs
git commit -m "feat: add BuildReplayScript for incremental DOM rebuild via JS"
```

---

### Task 3: Modify `WebChatView.xaml.cs` — Separate Shell Load from Message Render

**Files:**
- Modify: `src/Athlon.Agent.App/Controls/WebChatView.xaml.cs`

This is the core change. The current flow is:

```
LoadMessagesAsync() → EnsureInitializedAndRenderAsync() → NavigateHtmlAsync(fullHtml)
```

New flow:

```
First call:
  InitializeWebViewAsync()
    → NavigateToString(shellHtml)          ← one time, shell only (no events)
  LoadMessagesAsync()
    → WaitForDocumentReadyAsync()
    → ExecuteScriptAsync(replayEvents)      ← JS rebuilds the DOM

Subsequent calls:
  LoadMessagesAsync()
    → ExecuteScriptAsync(replayEvents)      ← no navigation at all
```

- [ ] **Step 1: Add `_shellLoaded` and `_shellPendingMessages` tracking fields**

Add to the field declarations (after line 35):

```csharp
private bool _shellLoaded;
private IReadOnlyList<ChatMessageViewModel>? _shellPendingMessages;
private bool _shellPendingShowToolCalls;
```

- [ ] **Step 2: Add `LoadShellAsync` to navigate to shell HTML once**

```csharp
/// <summary>
/// Navigates WebView to the shell HTML (CSS, JS libs, timeline script, empty state).
/// Called once during initialization. After this, all content updates happen via JS.
/// </summary>
private async Task<bool> LoadShellAsync(int expectedGeneration)
{
    var html = _htmlBuilder.BuildShellHtml(ResolveSsoDisplayName());
    var success = await NavigateHtmlAsync(html, expectedGeneration).ConfigureAwait(true);
    if (success)
    {
        _shellLoaded = true;
    }
    return success;
}
```

Note: `ShellHtml` already exists as `BuildShellHtml()` — it returns the full page HTML **without** messages (no replay script). The shell contains:
- Theme token CSS
- Code syntax override CSS
- Static shell styles
- Empty state div
- `marked.min.js` and `highlight.min.js`
- I18n bootstrap script
- Timeline script (`GetTimelineScript()` including `replayEvents`, `handleEvent`, etc.)

- [ ] **Step 3: Modify `InitializeWebViewAsync` to load shell**

After `ApplyThemeBackground();` and `_initialized = true;`, add:

```csharp
// Load the shell HTML once (all subsequent renders use ExecuteScriptAsync)
var shellGeneration = Interlocked.Increment(ref _renderGeneration);
_ = LoadShellAsync(shellGeneration);
```

- [ ] **Step 4: Modify `EnsureInitializedAndRenderAsync` to split two paths**

Replace the current full `EnsureInitializedAndRenderAsync` body:

```csharp
private async Task EnsureInitializedAndRenderAsync(int expectedGeneration)
{
    await EnsureReadyAsync().ConfigureAwait(true);
    if (!_needsRender || expectedGeneration != _renderGeneration)
    {
        return;
    }

    if (!CanRender())
    {
        if (!_loggedCanRenderBlock)
        {
            _loggedCanRenderBlock = true;
            App.StartupTrace(
                $"WebChatView CanRender=false (visible={IsVisible}, width={ActualWidth:0.##}, height={ActualHeight:0.##})");
        }
        return;
    }

    _loggedCanRenderBlock = false;

    if (_renderInProgress)
    {
        _renderQueuedWhileInProgress = true;
        return;
    }

    _renderInProgress = true;
    try
    {
        if (!_shellLoaded)
        {
            // First time: navigate to shell HTML, then replay events after ready
            var navigated = await LoadShellAsync(expectedGeneration).ConfigureAwait(true);
            if (!navigated || expectedGeneration != _renderGeneration)
            {
                return;
            }

            if (!await WaitForDocumentReadyAsync().ConfigureAwait(true))
            {
                _needsRender = true;
                return;
            }
        }

        if (expectedGeneration != _renderGeneration)
        {
            return;
        }

        // Replay events via JS — no full navigation
        var replayScript = _htmlBuilder.BuildReplayScript(_pendingMessages, _pendingShowToolCalls);
        await ChatWebView.CoreWebView2.ExecuteScriptAsync(replayScript).ConfigureAwait(true);

        _needsRender = false;
        App.StartupTrace($"WebChatView replayed {_pendingMessages.Count} messages (shellLoaded={_shellLoaded})");
    }
    finally
    {
        _renderInProgress = false;
        if (_needsRender && _renderQueuedWhileInProgress)
        {
            _renderQueuedWhileInProgress = false;
            ScheduleRenderRetry();
        }
        else
        {
            _renderQueuedWhileInProgress = false;
        }
    }
}
```

- [ ] **Step 5: Modify `NavigateHtmlAsync` to accept any HTML, not just full-document**

The current `NavigateHtmlAsync` already accepts arbitrary HTML — it passes it to `NavigateToString`. No changes needed to the method signature.

But we need to verify: after `ShellHtml` navigation, `_documentReady` is set properly. The existing `NavigateHtmlAsync` already handles this (line 460-476). ✅

- [ ] **Step 6: Handle edge case — WebView re-initialization**

If the WebView is unloaded and reloaded (tab switch, etc.), `_shellLoaded` needs to reset. In `OnUnloaded`:

```csharp
private void OnUnloaded(object sender, RoutedEventArgs e)
{
    _shellLoaded = false;
    _documentReady = false;
    // ... existing unsubscribe code ...
}
```

- [ ] **Step 7: Handle edge case — `SyncChatView` from streaming during active turn**

During streaming, `SyncChatView` is called on `OnMessagesCollectionChanged`. With the old approach, this triggered a full reload. With the new approach, `LoadMessagesAsync` will call `ExecuteScriptAsync(replayEvents)`. This is fine for session switches but **could disrupt active streaming** if the user is watching a live response.

However, looking at the current code: `SyncChatView` is only called when `_bulkChatViewSyncDepth == 0` (not inside a bulk sync). During streaming, individual events go through `DispatchToChatView` → `DispatchEventAsync` → `handleEvent` (incremental JS). So `SyncChatView` won't fire during streaming for individual messages. ✅

- [ ] **Step 8: Verify the render generation coordination is correct**

The existing `_renderGeneration` increment pattern:
- `LoadMessagesAsync` increments `_renderGeneration` and asks `RunRenderPipelineSafeAsync(generation)`
- If another `LoadMessagesAsync` comes before render completes, old generation is stale → skipped (line 235)
- `NavigateHtmlAsync` checks both `_navigationGeneration` AND `_renderGeneration` (line 483)

With the new approach, since we're no longer navigating, the `_navigationGeneration` check in `NavigateHtmlAsync` only matters for the initial shell load. For subsequent JS-only replays, we only need `_renderGeneration`. The pattern is correct. ✅

- [ ] **Step 9: Build and fix any compilation errors**

```bash
dotnet build src/Athlon.Agent.App/Athlon.Agent.App.csproj
```

Expected: 0 errors. Copy-related warnings are acceptable (app process locks).

- [ ] **Step 10: Commit**

```bash
git add src/Athlon.Agent.App/Controls/WebChatView.xaml.cs
git commit -m "perf: incremental WebView render — load shell once, replay messages via JS

Instead of calling NavigateToString (full page reload) on every session switch,
the shell HTML (CSS, JS libs, timeline script) is loaded once during WebView
initialization. All subsequent LoadMessagesAsync calls dispatch events via
ExecuteScriptAsync using the existing replayEvents() JS function, eliminating
the blank-screen delay and full DOM rebuild overhead."
```

---

### Verification

**Performance test:**

1. Start app, send several turns with tool-heavy messages (file_read, execute_command)
2. Note the time delay when switching sessions (before fix)
3. Apply fix
4. Start app fresh, same steps — switching sessions should be near-instant (sub-100ms vs potentially 500ms+)

**Functional test checklist:**

| Scenario | Expected Behavior |
|----------|------------------|
| First app launch, no messages | Empty state visible, no replay needed |
| Send a message, see streaming response | Streaming via `DispatchEventAsync` → `handleEvent` — **unchanged** |
| Switch to another session with history | Events replayed via `ExecuteScriptAsync`, all messages visible immediately |
| Switch back to the first session (with streaming in progress elsewhere) | Events replayed correctly, no stale data from previous session |
| Theme change | `ApplyThemeStylesAsync` → JS theme update — **unchanged** |
| I18n change | `ApplyI18nAsync` → JS i18n update — **unchanged** |
| App minimize/restore | WebView visibility toggle, retry mechanism still works |
| Rapid session switching | Generation counter prevents stale replays |

---

## Self-Review

**1. Spec coverage:**
- ✅ Separate shell from message rendering
- ✅ First load: NavigateToString(shellHtml)
- ✅ Subsequent loads: ExecuteScriptAsync(replayEvents)
- ✅ Existing streaming/DispatchEventAsync mechanism unaffected
- ✅ Theme/i18n changes unaffected

**2. Placeholder scan:** No placeholders found.

**3. Type consistency:**
- `BuildShellHtml()` → already exists, returns string
- `BuildReplayScript(messages, showToolCalls)` → returns string (JS code)
- `BuildReplayEventsJson(messages, showToolCalls)` → returns string (JSON array)
- `LoadShellAsync(expectedGeneration)` → returns Task<bool>
- `_shellLoaded` → boolean field
- All consistent with existing patterns.
