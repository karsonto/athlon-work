# Athlon Agent

Athlon Agent is a Windows AI Agent prototype built with .NET 8 WPF. The product goal is a Codex-like desktop agent that can chat with OpenAI-compatible models, inspect and edit a configured workspace, call built-in tools, and later connect to Skills and MCP servers.

## Current Status

- Codex-like WPF shell with chat timeline, settings view, right context sidebar, workspace selector, and multi-message tool output display.
- OpenAI-compatible chat completions client with function calling support.
- Shared `AgentRuntime` for prompt building, model calls, tool calls, and multi-step agent loops.
- Built-in filesystem tools: `file_list`, `file_read`, `file_write`, `file_edit`, `grep_files`, `glob_files`, `execute_command`.
- File-first persistence for settings, sessions, logs, credentials, and audit records.
- API key persistence through Windows DPAPI instead of plain JSON.
- Markdown chat rendering through `MdXaml`.
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

## Local Data Path

Runtime data is stored under the current Windows user profile, not `%LocalAppData%`:

```text
C:\Users\<UserName>\.athlon-agent\
  config\        settings JSON
  skills\        AgentScope-style skill folders (<name>/SKILL.md + resources)
  sessions\      Markdown session history and metadata
  logs\          Serilog logs and startup diagnostics
  credentials\   DPAPI encrypted API key files
  audit\         JSONL tool-call audit logs
```

The path is provided by `AppPathProvider` in `src/Athlon.Agent.Infrastructure/CommonInfrastructure.cs`. Keep new persistence code behind `IAppPathProvider` instead of hardcoding paths.

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
- `file_read`: read file content with line numbers and optional range parameters.
- `file_write`: create or overwrite files after workspace guard validation.
- `file_edit`: replace exact text, with optional `replace_all`.
- `grep_files`: search file contents in the workspace.
- `glob_files`: find workspace files by glob pattern.
- `execute_command`: enabled by default; subject to command deny-list rules.

All file tools should respect workspace boundaries through `WorkspaceGuard`. Writes and edits create backups through `AtomicFile`.

- `compress`: manually compact conversation context and end the current agent turn.

## Context Compression

Before each model call, `PreCompletionPipeline` runs:

1. **Microcompact** (always): older `Tool` message bodies are replaced with `[cleared]`; keep the last 5 tool outputs below 50% of the context window, or the last 3 at/above 50%.
2. **Auto-compact** (when estimated tokens reach 80% of `contextWindowTokens`, default 256K → 204.8K): full history is archived to `sessions/<sessionId>/transcripts/transcript_<unix>.jsonl`, summarized by the model, then replaced with a single user message `[Compressed. Transcript: <path>]\n<summary>`.

Configure in `~/.athlon-agent/config/settings.json` under `contextCompaction` (`contextWindowTokens`, `autoCompactThresholdRatio`, `microcompactAggressiveRatio`, etc.).

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
