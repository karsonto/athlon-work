# Context Compression

Before each model call, `PreCompletionPipeline` runs a **budget-aware parameter adjuster** (when `contextCompaction.dynamicCompaction.enabled` is true, the default). Dynamic mode **raises LLM compact thresholds** toward **`targetUtilization` (default 0.80)** — static message/token compact limits do **not** apply while dynamic mode is on. Truncate/re-evict still honor static floors. After a full **3-level pass**, history lands near **`postCompactionUtilization` (default 0.45)**. Set `contextWindowTokens` to the real model window when it differs; the default is `131072` (128K tokens).

Pressure uses **`TotalUtilization`** = `(FixedOverhead + EstimatedHistory) / UsablePromptWindow` (full prompt vs usable window). Do not confuse it with **`HistoryUtilization`** = `EstimatedHistory / HistoryBudget`, which is informational only.

| Pressure | TotalUtilization vs target (default 80%) | Actions |
|----------|------------------------------------------|---------|
| Normal | &lt; ~55% absolute | none (no LLM compact) |
| Elevated | ~55–72% absolute | prefix re-evict only (no truncateArgs, no LLM compact) |
| High | ≥ 72% (= target × 0.90) | truncateArgs + prefix re-evict (static keep floor) |
| Critical | ≥ 80% (= target) | full 3-level pass → ~45% post-compaction |
| Overflow | API `context_length` error | force compact → ~20% post-compaction + retry once |

By default, `contextCompaction.enabled` and `dynamicCompaction.enabled` are **true**. A `settings.json` that explicitly sets either flag to `false` keeps proactive compaction off. API overflow retry still compacts when needed.

When dynamic compaction is disabled (but proactive compaction is enabled), only the static thresholds below apply.

## Static layers

1. **truncateArgs** (non-LLM): when history reaches the truncate threshold (default: 25 messages / 40k estimated tokens), clips large tool argument strings on assistant messages outside the keep window (default: last 20 messages, max arg length 2000).
2. **conversation compact**: when history reaches the compact threshold (default: 50 messages / 80k estimated tokens), archives the session to `sessions/<sessionId>/transcripts/transcript_<unix>.jsonl`, summarizes the prefix (including any prior `__compaction_summary__` placeholders), then replaces it with an optional `Compaction` audit message plus a summary user placeholder (`__compaction_summary__`) and the preserved tail. If summarization fails or returns empty, history is left unchanged.
3. **tool result eviction** (after each tool invoke): if a tool result exceeds 80k characters, the full body is written to `sessions/<sessionId>/evicted/<toolCallId>.txt` and only a head/tail preview is kept in the in-memory tool message.

**Send-boundary hygiene** (always on by default): before each model API call, `RequestHistoryHygiene` compacts oversized tool payloads in the outbound request only. `publish_plan` `body` stays verbatim until a later approved-plan message makes that copy redundant; the plan text is still reattached from the compaction anchor.

Summary calls send no tool schemas. Read and search tool results in the summary input collapse to a one-line trace; write, edit, patch, and shell results keep a longer body. An approved plan that falls inside the summarized span is copied back after the summary. Runtime context updates append `Runtime context updated.` instead of replaying the previous context.

## Configuration

See `~/.athlon-agent/config/settings.json` under `contextCompaction`. Full example and field descriptions are in the [repository README](../../README.md#configuration) or copy from `src/Athlon.Agent.Core/AgentSettings.cs`.
