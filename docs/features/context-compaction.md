# Context Compression

Athlon uses a **single CONTEXT CHECKPOINT** path (Codex-style), not a multi-tier prune ladder.

Before each model call, `CompactionTurnMiddleware` → `PreCompletionPipeline` decides whether to checkpoint. When it does, `ConversationCompactor` replaces session history with:

1. A summary placeholder (`SummaryResumePrefix` + CONTEXT CHECKPOINT body)
2. Optional `[session-handoff]` note (file side-channel)
3. Recent **real user** messages within `RetainRecentUserMaxTokens` (default 20 000; Overflow/Force uses `OverflowRetainRecentUserMaxTokens`, default 8 000)
4. A pairing-safe open tool tail only when the conversation ends with unclosed `tool_calls`

System / runtime context are **not** stored in session messages. Each round still gets canonical context via `PrepareForTurn` + `BuildRuntimeContext` + `ModelMessagesForApiBuilder`.

## When checkpoint runs

| Trigger | Behavior |
|---------|----------|
| Dynamic on + `TotalUtilization >= TargetUtilization` (default 80%) | Checkpoint |
| Overflow (`context_length`) / Force / Manual | Checkpoint with tighter user retention |
| `FixedOverhead >= 0.85 * UsablePromptWindow` | Skip empty history compact (`fixed_overhead_dominates`) |

Pressure bands Elevated/High remain for UI metering only; they do **not** run truncate/re-evict before compact.

## Summary

- Built-in CONTEXT CHECKPOINT / handoff prompt (settings `summaryPrompt` is ignored).
- Overflow/Force: LLM empty/failure → retry once → `TemplateSummaryBuilder`.
- Critical: failure → skip (`summary_failed`).
- `CompactionQualityWarnAfterCount` (default 2): warn when a session accumulates many summaries.

## Still orthogonal (kept)

- Per-tool `ToolResultEvictor` on tool return
- `RequestHistoryHygiene` on the outbound API payload
- `SessionHandoffNote` / `session_note_append`

## Configuration

`~/.athlon-agent/config/settings.json` → `contextCompaction`:

- `retainRecentUserMaxTokens`, `overflowRetainRecentUserMaxTokens`
- `compactionQualityWarnAfterCount`
- `enableTemplateSummaryOnOverflowFailure`, `fixedOverheadDominatesRatio`
- `dynamicCompaction.targetUtilization`, `contextWindowTokens`

Removed (no longer used): middle-cut, pressure prune divisors, semantic protected-tail / must_preserve / plan-user reattach multi-path, PreCompletion truncateArgs.
