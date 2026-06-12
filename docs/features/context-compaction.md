# Context Compression

Before each model call, `PreCompletionPipeline` runs a **budget-aware parameter adjuster** (when `contextCompaction.dynamicCompaction.enabled` is true). Dynamic mode **raises LLM compact thresholds** toward **`targetUtilization` (default 0.80)** — static message/token compact limits do **not** apply while dynamic mode is on. Truncate/re-evict still honor static floors. After a full **3-level pass**, history lands near **`postCompactionUtilization` (default 0.30)**.

| Pressure | Utilization vs target (default 80%) | Actions |
|----------|-------------------------------------|---------|
| Normal | &lt; ~55% absolute | none (no LLM compact) |
| Elevated | ~55–72% absolute | none (no LLM compact) |
| High | ≥ 72% (= target × 0.90) | truncateArgs + optional prefix re-evict (static keep floor) |
| Critical | ≥ 80% (= target) | full 3-level pass → ~30% post-compaction |
| Overflow | API `context_length` error | force compact → ~20% post-compaction + retry once |

By default, `contextCompaction.enabled` and `dynamicCompaction.enabled` are **false**: proactive compaction is off until you enable it in settings or `settings.json`. API overflow retry still compacts when needed.

When dynamic compaction is disabled (but proactive compaction is enabled), only the static thresholds below apply.

## Static layers

1. **truncateArgs** (non-LLM): when history reaches the truncate threshold (default: 25 messages / 40k estimated tokens), clips large tool argument strings on assistant messages outside the keep window (default: last 20 messages, max arg length 2000).
2. **conversation compact**: when history reaches the compact threshold (default: 50 messages / 80k estimated tokens), archives the session to `sessions/<sessionId>/transcripts/transcript_<unix>.jsonl`, summarizes the prefix, then replaces it with an optional `Compaction` audit message plus a summary user placeholder (`__compaction_summary__`) and the preserved tail.
3. **tool result eviction** (after each tool invoke): if a tool result exceeds 80k characters, the full body is written to `sessions/<sessionId>/evicted/<toolCallId>.txt` and only a head/tail preview is kept in the in-memory tool message.

**Send-boundary hygiene** (always on by default): before each model API call, `RequestHistoryHygiene` compacts oversized tool payloads in the outbound request only.

## Configuration

See `~/.athlon-agent/config/settings.json` under `contextCompaction`. Full example and field descriptions are in the [repository README](../../README.md#configuration) or copy from `src/Athlon.Agent.Core/AgentSettings.cs`.
