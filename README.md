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

In `config/settings.json`, optional `AgentTurn.TimeoutMinutes` controls how long a single user message may run (agent tool loop included). Default is **30**; positive values are clamped to **1–180**. Set **`0`** to disable the turn timeout (no automatic stop for long agent loops). Example for a two-hour run:

```json
{
  "AgentTurn": {
    "TimeoutMinutes": 120
  }
}
```

Unlimited turn (only user **Stop** ends the run):

```json
{
  "AgentTurn": {
    "TimeoutMinutes": 0
  }
}
```

Changes apply on the next send after saving or editing the file (restart the app if settings were only changed on disk while running).

### Plan auto-continue

When a turn ends (including turn timeout) and the session plan still has an **in-progress** subtask, Athlon may automatically start another turn with a continue instruction. User **Stop** does not trigger auto-continue. **Clear context** clears the in-memory plan and deletes `plan.md` in the active workspace.

```json
{
  "Plan": {
    "AutoContinueEnabled": true,
    "MaxAutoContinueRounds": 20,
    "MaxSubtasks": 20
  }
}
```

Long-running work should use `create_plan` with granular subtasks (see system prompt / README). `MaxAutoContinueRounds` limits how many automatic continue turns run after a single manual user message.

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
- API key: stored locally with DPAPI under the user profile data path.

The agent sends an environment prompt that includes active workspace path and available tools. It does not inject a static workspace file snapshot; the model should use `file_list`, `grep_files`, or `glob_files` for fresh file information.

## Built-In Tool Rules

- `file_list`: list current workspace files and directories.
- `file_read`: stream-read file content with `N|line` prefixes; default 500 lines per call (max 2000), 32KB response cap, 2MB file size cap. Use `offset`/`limit` or `start_line`/`end_line` for large files; prefer `grep_files` first.
- `file_write`: create or overwrite files after workspace guard validation.
- `file_edit`: replace exact on-disk text (auto-strips accidental `file_read` line prefixes); optional `replace_all`.
- `grep_files`: search file contents in the workspace.
- `glob_files`: find workspace files by glob pattern.
- `execute_command`: runs via `cmd.exe /c`; default wait **3600s (1 hour)**, max **3600s** (`timeout` argument). Command timeout returns a tool failure and does **not** stop the agent turn. User **Stop** kills the command process tree. Subject to command deny-list rules. For runs longer than one hour, split work or raise `AgentTurn.TimeoutMinutes` / set it to `0` when appropriate.

All file tools should respect workspace boundaries through `WorkspaceGuard`. Writes and edits create backups through `AtomicFile`.

## Context Compression

Before each model call, `PreCompletionPipeline` runs (AgentScope-style, no hooks):

1. **truncateArgs**: truncates oversized string arguments in `ToolCallsJson` on assistant messages outside the keep window (default: trigger at 25 messages / 40k tokens, keep last 20, max arg length 2000). No LLM call.
2. **conversation compact**: when history reaches the trigger (default: 50 messages / 80k estimated tokens), archives the prefix to `sessions/<sessionId>/transcripts/transcript_<unix>.jsonl`, summarizes it with the model, then replaces the prefix with a compaction audit message plus a summary user placeholder (`__compaction_summary__`) and keeps the tail messages intact. Safe cutoff never splits an assistant/tool pair.
3. **tool result eviction** (after each tool invoke): if a tool result exceeds 80k characters, the full body is written to `sessions/<sessionId>/evicted/<toolCallId>.txt` and only a head/tail preview is kept in the in-memory tool message. `file_write`, `file_edit`, `grep_files`, `glob_files`, and `file_list` are excluded by default; `file_read` is included so oversized reads do not blow the context window.

On context-length API errors, the runtime forces one conversation compact and retries the model call once.

When compaction runs, the app appends a persisted `Compaction` role message and shows it in chat as a collapsible card. Summary user placeholders are hidden in the UI. Compaction messages are not sent to the model API.

Configure in `~/.athlon-agent/config/settings.json` under `contextCompaction`:

```json
"contextCompaction": {
  "contextWindowTokens": 256000,
  "triggerMessages": 50,
  "triggerTokens": 80000,
  "keepMessages": 20,
  "summaryPrompt": "...",
  "truncateArgs": { "triggerMessages": 25, "triggerTokens": 40000, "keepMessages": 20, "maxArgLength": 2000 },
  "toolResultEviction": { "maxResultChars": 80000, "previewChars": 2000 }
}
```

Legacy fields (`microcompactKeepToolMessages`, `autoCompactThresholdRatio`, etc.) are ignored after migration.

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
