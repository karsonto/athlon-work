# Session-switch architecture

How the desktop shell switches between chat sessions, what is cached, how the caches are
invalidated, which switches can roll the optimization back, and where the code lives after the
"no more giant files" split.

## Goals

- Revisiting an already-rendered session should feel instant (target: p50 <= 100 ms / p95 <= 200 ms)
  without regressing a cold session's first paint.
- Switching must not flash, and per-session composer drafts and scroll position must survive.
- Streaming, turn folding, tool approval, plan cards and FILES_CHANGED behave exactly as before.
- The hot files stay small: new logic goes into focused files, never back into the giants.

## Switch path

```
MainShellViewModel.SessionSwitch.cs     OpenSessionByIdAsync / LoadSessionInternalAsync
  -> PrepareSessionForSwitchAsync       save outgoing draft, persist
  -> LoadFullSessionAndAdoptAsync       hydrate the display window (paged)
  -> SwitchDisplayedSession             reuse the single WebChatView (no re-create)
  -> SessionTurnUiController.SyncChatView(immediate)
     -> SessionTurnUiController.Display.cs   HydrateDisplayAsync (vm rebuild)
     -> WebChatView.LoadMessagesAsync        replay shards -> WebView2
        -> timeline-protocol.js replayEvents -> timeline-*.js render
  -> ApplyLoadedSessionChrome           restore draft, clear attachments
```

`SessionSwitchProfiler` records each stage (`prepare`, `snapshotLoad`, `vmRebuild`, `replayBuild`,
`postBatches`, `jsRender`, `firstPaint`) and emits one structured `session.switch` event
(`IAppLogger` + `RuntimeDiagnostics`) carrying the session id, the hit kind
(`cold` / `replay` / `snapshot`) and the per-stage milliseconds. This is how the p50/p95 targets are
measured; no user-visible behaviour depends on it.

## Caching levels

Resolution order for a session turn:

1. **C# replay-shard cache** (implemented) - `ChatReplaySnapshotCache`
   - key: `sessionId` -> `{ revision, batches }`; LRU capacity 4, aligned with `SessionUiCache`.
   - `revision` is a content fingerprint (`ChatReplayRevision`, SHA-256 over message ids, roles,
     statuses, content, plan state, tool-call state and `CultureInfo.CurrentUICulture`).
   - On hit, `WebChatView` re-posts the already-serialized shards and skips
     `ChatEventSerializer.BuildReplayEvents` entirely.
2. **Markdown HTML memoization** (implemented) - `MarkdownHtmlRenderer.ToHtmlFragment`
   - content-hash LRU (256 entries, 32 KiB/entry) so even a cold session avoids re-rendering the
     same Markdown fragment twice.
3. **JS per-session DOM snapshot** (not yet implemented) - planned follow-up: move the `#messages`
   children into a per-session `root` so a hit swaps the subtree instead of rebuilding it. The JS
   is already split into modules so this can land without touching a monolith.

## Invalidation matrix (conservative: unsure => miss)

| Event | Effect |
|---|---|
| Turn completes / streamed content changes | `revision` changes (content) -> miss |
| `Remove` / replace of a session's controller | `SessionUiCache.Remove` also drops the replay cache entry |
| Manual or automatic compaction | compaction card content + messages change -> miss |
| Session deleted | controller + replay cache entry dropped |
| Plan run added / removed / advanced | plan fields are part of `revision` -> miss |
| UI culture changes (i18n) | culture name is part of `revision` -> miss |
| Tool-call status / approval state changes | part of `revision` -> miss |
| `CacheReplayEvents` switch off | `TryGet` always misses; `Set` is discarded |

## Rollback switches (`UiSettings`)

| Switch | Default | Effect when `false` |
|---|---|---|
| `Ui.FastSessionSwitch` | `true` | Master switch: disables the C# replay cache end to end (old replay path). |
| `Ui.CacheReplayEvents` | `true` | Granular: disables only the C# replay-shard cache. |
| `Ui.PreserveSessionUiState` | `true` | Restores clear-composer-on-switch and scroll-to-bottom-on-switch. |

`ChatReplaySnapshotCache.Enabled` is initialised to `FastSessionSwitch && CacheReplayEvents`.
`SessionTurnUiController.PreserveSessionScroll` (fed from `PreserveSessionUiState`) decides whether
the replay command carries the session id that lets the JS restore a saved scroll position.

## Chat render protocol (unchanged)

`WebChatView` -> JS commands keep their existing shape; the switch work only adds a trailing
`sessionId` on the first replay shard and a `replayComplete` message carrying `renderMs`:

| Command / message | Notes |
|---|---|
| `replayEvents` | batched shards; first shard may carry `sessionId` (enables scroll restore) |
| `appendEvents` / `prependEvents` | incremental live updates / older-page prepend |
| `replayComplete` | `{ renderGeneration, renderMs }`; drives `jsRender` profiling |
| `historyAvailability` | tells the shell whether older pages exist |

## Where the code lives

Hot files and their focused parts (all equivalent moves; no signature or key changes):

| Was | Now |
|---|---|
| `ViewModels/MainShellViewModel.cs` (3500) | `.SessionSwitch.cs`, `.Sessions.cs`, `.TurnEvents.cs`, `.Workspace.cs`, `.Editor.cs`, `.Chrome.cs`, `.ComputerUse.cs` (+ pre-existing `.SessionPersistence.cs`, `.SshTransfer.cs`); main file 1111 |
| `Services/SessionTurnUiController.cs` (2128) | `.Display.cs`, `.Streaming.cs`, `.Activity.cs`, `.TurnLifecycle.cs`, `.Plan.cs` (+ pre-existing `.Approvals.cs`, `.Compaction.cs`); main file 739 |
| `Assets/Chat/chat-timeline.js` (2697) | `timeline-state.js`, `timeline-render.js`, `timeline-cards.js`, `timeline-protocol.js` (loaded in that order) |
| `Themes/Controls.xaml` (90 KB) | `Controls.Buttons.xaml`, `Controls.Menus.xaml`, `Controls.Inputs.xaml`, `Controls.Sidebar.xaml` |
| `Themes/ChatStyles.xaml` (44 KB) | `ChatStyles.Composer.xaml`, `ChatStyles.Panels.xaml` |

Load/merge order is preserved everywhere, so `StaticResource`/`DynamicResource` resolution and
`x:Key` lookup are unchanged. Splitting the timeline JS required no script reordering because the
only top-level statements already lived at the end of the file.

## Verification

- `dotnet build src/Athlon.Agent.App/Athlon.Agent.App.csproj -c Release` - 0 errors.
- `dotnet test tests/Athlon.Agent.Tests/Athlon.Agent.Tests.csproj -c Release` - the chat/replay,
  controller, theme and JS-contract suites all pass.
- XAML split checked by comparing the ordered `x:Key` / `TargetType` lists against `HEAD`
  (identical counts and sequence).
