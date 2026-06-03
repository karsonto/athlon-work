# Athlon Agent

Athlon Agent is a Windows AI Agent prototype built with .NET 8 WPF. The product goal is a Codex-like desktop agent that can chat with OpenAI-compatible models, inspect and edit a configured workspace, call built-in tools, and later connect to Skills and MCP servers.

## Current Status

- Codex-like WPF shell with chat timeline, settings view, right context sidebar, workspace selector, and multi-message tool output display.
- OpenAI-compatible chat completions client with function calling support.
- Shared `AgentRuntime` for prompt building, model calls, tool calls, and multi-step agent loops.
- Built-in filesystem tools: `file_list`, `file_read`, `file_write`, `file_edit`, `grep_files`, `glob_files`, `execute_command`.
- File-first persistence for settings, sessions, logs, credentials, and audit records.
- API key persistence through Windows DPAPI instead of plain JSON.
- Markdown chat rendering through `MdXaml` (Mermaid blocks stay as code; right-click **查看 Mermaid 图表** opens an offline preview dialog bundled with `mermaid.min.js`).
- Skill YAML loading and Handlebars rendering foundation.
- MCP configuration UI and stdio client skeleton.
- GitHub Actions CI and release packaging for installer exe and portable zip.

## Tech Stack

- .NET 8 / C# / WPF
- MVVM with `CommunityToolkit.Mvvm`
- Dependency injection with `Microsoft.Extensions.DependencyInjection`
- Markdown rendering with `MdXaml`
- Logging with `Serilog`
- YAML with `YamlDotNet`
- Skill templates with `Handlebars.Net`
- Tests with xUnit
- Installer packaging with Inno Setup 6

## Project Structure

```text
src/
  Athlon.Agent.App/             WPF UI, views, view models, app startup
  Athlon.Agent.Core/            Domain models, settings, agent runtime interfaces
  Athlon.Agent.Infrastructure/  Storage, logging, model client, tools, DPAPI
  Athlon.Agent.Mcp/             MCP client foundation
  Athlon.Agent.Skills/          Skill loading and template rendering
tests/
  Athlon.Agent.Tests/           xUnit tests
.github/workflows/
  ci.yml                       Build validation on push / PR
  release.yml                  Tag-based GitHub Release packaging
```

## License (AD-bound)

Athlon Agent requires a signed license bound to the current Windows AD account (Sam `DOMAIN\user` and/or UPN `user@domain.com`). On startup the app verifies signature, expiry, and account match.

**License file locations** (first existing file wins):

1. Next to `Athlon.Agent.App.exe`: `license.lic` (optional IT deployment)
2. `%USERPROFILE%\.athlon-agent\config\license.lic` (user activation save path)

If validation fails, an activation dialog lets the user paste or import a `.lic` file; on success the license is saved to the user config path.

**Issue licenses** (admin machine with the private key):

```bash
cd tools/license
pip install -r requirements.txt
python generate_keys.py          # first time only; sync public.pem to LicensePublicKey.cs
python generate_license.py --account "CONTOSO\\jdoe" --days 30 --output license.lic
```

See [`tools/license/README.md`](tools/license/README.md) for full options (`--expires`, `--sam`, `--upn`).

**Debug skip** (Debug builds only): set environment variable `ATHLON_SKIP_LICENSE=1`.

This is offline signature validation for internal compliance, not DRM.

## Local Data Path

Runtime data is stored under the current Windows user profile, not `%LocalAppData%`:

```text
C:\Users\<UserName>\.athlon-agent\
  config\        settings JSON and license.lic
  skills\        AgentScope-style skill folders (<name>/SKILL.md + resources)
  sessions\      Markdown session history and metadata
  logs\          Serilog logs and startup diagnostics
  credentials\   DPAPI encrypted API key files
  audit\         JSONL tool-call audit logs
```

The path is provided by `AppPathProvider` in `src/Athlon.Agent.Infrastructure/CommonInfrastructure.cs`. Keep new persistence code behind `IAppPathProvider` instead of hardcoding paths.

### Agent turn timeout

In `config/settings.json`, optional `AgentTurn.TimeoutMinutes` controls how long a single user message may run (agent tool loop included). Default is **`0`** (disabled — only user **Stop** ends the run). Positive values are clamped to **1–180**. Example for a two-hour run:

```json
{
  "AgentTurn": {
    "TimeoutMinutes": 120
  }
}
```

Changes apply on the next send after saving or editing the file (restart the app if settings were only changed on disk while running).

## Run

```powershell
dotnet run --project src/Athlon.Agent.App/Athlon.Agent.App.csproj
```

## Build And Test

```powershell
dotnet build Athlon.Agent.slnx
dotnet test Athlon.Agent.slnx
```

When the app is running and locks output files, build with a temporary output directory:

```powershell
dotnet build src/Athlon.Agent.App/Athlon.Agent.App.csproj -p:OutDir=F:\athlon-work\artifacts\verify\out\
```

## Packaging

Local installer packaging requires Inno Setup 6:

```powershell
.\build.bat
```

The script publishes a self-contained Windows x64 build to `publish/` and creates an installer under `installer/`.

## GitHub Release

GitHub Actions builds and publishes release artifacts automatically when a tag like `v1.0.0` is pushed:

```powershell
git tag v1.0.0
git push origin v1.0.0
```

The release workflow uploads:

- `AthlonAgent_Setup_v*.exe`
- `AthlonAgent-Portable-x64.zip`

## Model Configuration

Configure the model in the app Settings view:

- Base URL: OpenAI-compatible endpoint, such as OpenAI, DeepSeek, Ollama, or LM Studio.
- Model name: chat model identifier.
- Max tokens: optional `max_tokens` for chat completions (empty = API default). Context summarization uses `contextCompaction.summaryMaxTokens` instead.
- API key: stored locally with DPAPI under the user profile data path.

The agent sends an environment prompt that includes active workspace path and available tools. It does not inject a static workspace file snapshot; the model should use `file_list`, `grep_files`, or `glob_files` for fresh file information.

## Built-In Tool Rules

- `file_list`: list current workspace files and directories.
- `file_read`: stream-read file content with `N|line` prefixes; default 500 lines per call (max 2000), 32KB response cap, 2MB file size cap. Use `offset`/`limit` or `start_line`/`end_line` for large files; prefer `grep_files` first.
- `file_write`: create or overwrite files after workspace guard validation.
- `file_edit`: replace exact on-disk text (auto-strips accidental `file_read` line prefixes); optional `replace_all`.
- `grep_files`: search file contents in the workspace.
- `glob_files`: find workspace files by glob pattern.
- `execute_command`: runs via `cmd.exe /c`; default wait **3600s (1 hour)**, max **3600s** (`timeout` argument). Command timeout returns a tool failure and does **not** stop the agent turn. User **Stop** kills the command process tree. Subject to command deny-list rules. For runs longer than one hour, split work or raise `AgentTurn.TimeoutMinutes` when a turn cap is configured.

All file tools should respect workspace boundaries through `WorkspaceGuard`. Writes and edits create backups through `AtomicFile`.

## Context Compression

Before each model call, `PreCompletionPipeline` runs a **budget-aware parameter adjuster** (when `contextCompaction.dynamicCompaction.enabled` is true). Dynamic mode does **not** define a separate strategy — it keeps the static layers below and auto-tunes their effective triggers and keep windows from a single target: **`targetUtilization` (default 0.80 = 80% of the usable context window)**. System prompt, tools schema, output reservation, and safety margin are subtracted first; the adjuster then decides when to act and how much history to keep so the full prompt stays near that ceiling.

| Pressure | Utilization vs target (default 80%) | Actions (same static layers, adjusted params) |
|----------|-------------------------------------|------------------------------------------------|
| Normal | &lt; ~55% absolute | none unless static thresholds fire |
| Elevated | ~55–72% absolute | none unless static thresholds fire |
| High | ≥ 72% (= target × 0.90) | truncateArgs + optional prefix re-evict |
| Critical | ≥ 80% (= target) | truncateArgs + re-evict + LLM conversation compact |
| Overflow | API `context_length` error | force compact at target × 0.50 keep + retry once |

Static thresholds always remain a **floor** — dynamic never compacts later than static when history is already long enough. When system/tools overhead is large, budget-aware triggers can act **earlier** so the window still approaches ~80% instead of waiting for message-count limits alone.

When dynamic compaction is disabled, only the static thresholds below apply.

Static layers:

1. **truncateArgs** (non-LLM): when history reaches the truncate threshold (default: 25 messages / 40k estimated tokens), clips large tool argument strings on assistant messages outside the keep window (default: last 20 messages, max arg length 2000).
2. **conversation compact**: when history reaches the compact threshold (default: 50 messages / 80k estimated tokens), archives the session to `sessions/<sessionId>/transcripts/transcript_<unix>.jsonl`, summarizes the prefix, then replaces it with an optional `Compaction` audit message plus a summary user placeholder (`__compaction_summary__`) and the preserved tail. Cutoff uses keep windows and never splits assistant/tool pairs. Semantic cutoff can inject `<must_preserve>` hints into the summary prompt for high-scoring prefix messages (user goals, file paths, write/edit commands).
3. **tool result eviction** (after each tool invoke): if a tool result exceeds 80k characters, the full body is written to `sessions/<sessionId>/evicted/<toolCallId>.txt` and only a head/tail preview is kept in the in-memory tool message. `file_write`, `file_edit`, `grep_files`, `glob_files`, and `file_list` are excluded by default; `file_read` is included so oversized reads do not blow the context window.

On context-length API errors, the runtime forces compaction at **Overflow** pressure and retries once, rebuilding the iteration system prompt after compact.

When compaction runs, the app appends a persisted `Compaction` role message and shows it in chat as a collapsible card (including pressure level and utilization when available). Summary placeholders are hidden in the UI but sent to the model as user messages. `Compaction` audit messages are not sent to the model API.

API `usage.prompt_tokens` (when returned) feeds a session-level EMA calibrator that adjusts token estimates over time.

Configure in `~/.athlon-agent/config/settings.json` under `contextCompaction`:

```json
"contextCompaction": {
  "contextWindowTokens": 256000,
  "compactTriggerRatio": 0.7,
  "triggerMessages": 50,
  "triggerTokens": 80000,
  "keepMessages": 20,
  "includeReasoningInModelContext": false,
  "summaryPrompt": "...",
  "truncateArgs": { "triggerMessages": 25, "triggerTokens": 40000, "keepMessages": 20, "maxArgLength": 2000 },
  "toolResultEviction": { "maxResultChars": 80000, "previewChars": 2000 },
  "dynamicCompaction": {
    "enabled": true,
    "targetUtilization": 0.80,
    "safetyMarginRatio": 0.08,
    "defaultReservedOutputTokens": 8192,
    "truncateLeadRatio": 0.90,
    "overflowKeepRatio": 0.50,
    "enableSemanticCutoff": true,
    "enableUsageCalibration": true
  }
}
```

Compaction triggers when message count **or** estimated token thresholds are reached. With dynamic compaction enabled, pressure uses `TotalUtilization = (system + tools + margin + history) / (contextWindow − reservedOutput)`; static thresholds remain the floor. When dynamic compaction is disabled, the compact token threshold is `max(triggerTokens, contextWindowTokens × compactTriggerRatio)`.

By default, `includeReasoningInModelContext` is **false**: assistant thinking chains are shown in the UI and saved to `conversation.jsonl`, but are **not** sent back in API history (saves tokens). Set to `true` only if your model requires historical `reasoning_content`.

## Session Disk Logs

Per session under `~/.athlon-agent/sessions/<sessionId>/`:

| Path | Content |
|------|---------|
| `session.json` | Full session snapshot (updated after each message during a turn) |
| `conversation.md` | Human-readable transcript (rewritten on each snapshot) |
| `conversation.jsonl` | One JSON line per message as it is added |
| `tool-calls/calls.jsonl` | One JSON line per tool invocation (name, args, result, duration) |
| `http/interactions.jsonl` | One JSON line per chat/completions HTTP call (request redacted, response truncated) |
| `transcripts/transcript_<unix>.jsonl` | Full history archive before auto-compact |

HTTP log lines include timestamp, endpoint, purpose (`chat-completion` or `context-summary`), HTTP status, duration, sanitized request JSON, response body (truncated for large/error bodies), and error text. Global Serilog files remain under `~/.athlon-agent/logs`. Workspace tool side effects also append to `~/.athlon-agent/audit/audit-<date>.jsonl`.

## Notes For Future AI Work

- Prefer extending `AgentRuntime`, `AgentEnvironmentPromptBuilder`, and tool classes instead of adding model logic to the WPF layer.
- Keep UI logic in focused files under `Athlon.Agent.App/ViewModels/` following the existing MVVM pattern.
- Keep persistence file-based unless there is a strong product reason to introduce a database.
- Do not reintroduce `%LocalAppData%` or `AthlonAgent` for default app data. Use `IAppPathProvider` (folder name: `.athlon-agent` under the user profile).
- Before editing `.pen` design files, use the Pencil MCP tools only.
- After substantive edits, run `dotnet build` and check lints for changed files.

## High-Value Next Improvements

- Add tests for `AppPathProvider`, workspace guard behavior, and filesystem tools.
- Implement real MCP server lifecycle: connect, tools/list, tools/call, status updates, and error display.
- Add command execution confirmation UI before `execute_command` runs.
- Add session branch management.
- Improve Markdown/code rendering with copy/run/diff actions for code blocks.
- Add installer icon, version metadata, and optional code signing to the release workflow.
