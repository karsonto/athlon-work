<div align="center">

# Athlon Agent

**A native Windows desktop AI coding agent — local-first, OpenAI-compatible, built with .NET 10 WPF.**

Chat with any LLM, explore and edit your workspace, run tools in an agent loop, schedule recurring tasks, and extend with Skills & MCP — all from a polished desktop app.

[![.NET 10](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)
[![Platform](https://img.shields.io/badge/platform-Windows-0078D4?logo=windows&logoColor=white)](https://github.com/karsonto/athlon-work)
[![WPF](https://img.shields.io/badge/UI-WPF-68217A)](https://github.com/karsonto/athlon-work)
[![CI](https://img.shields.io/github/actions/workflow/status/karsonto/athlon-work/ci.yml?branch=main&label=CI)](https://github.com/karsonto/athlon-work/actions)

[Features](#-features) · [Quick Start](#-quick-start) · [Screenshots](#-screenshots) · [Architecture](#-architecture) · [Contributing](#-contributing) · [Docs](#-documentation)

If Athlon Agent saves you time, consider giving it a **⭐** — it helps others discover the project.

</div>

---

## ✨ Why Athlon Agent?

Most AI coding assistants are either web-only or Electron-heavy. Athlon Agent is different:

| | |
|---|---|
| **Native Windows** | Real WPF shell with WebView2 chat rendering — fast, polished desktop UI |
| **Bring your own model** | OpenAI-compatible APIs (OpenAI, DeepSeek, Ollama, LM Studio, …) |
| **Agent loop built-in** | Multi-step tool calling with filesystem, grep, glob, shell |
| **Token-smart** | Dynamic context compaction pipeline, hygiene, eviction, and MCP tool search |
| **Extensible** | Skills (YAML + Handlebars), MCP servers, sub-agent delegation |
| **Private by default** | Settings, sessions, and API keys stay under your user profile (DPAPI) |

---

## 🚀 Features

### Chat & Workspace
- Codex-like chat timeline with tool-call cards, reasoning display, and session history
- Multi-workspace support with file tree, in-app editor (AvalonEdit), and workspace guard
- Native Markdown rendering (MdXaml) with code-block copy, Mermaid offline preview
- Light / dark themes with consistent Indigo accent ([theme conventions](docs/development/theme-and-ui-conventions.md))
- **Composer** with `@`-mention file/symbol completion, slash commands, and image paste
- **Full localization**: zh-CN (default) and en-US via `.resx` resources

### Agent Runtime
- Shared `AgentRuntime`: prompt building, streaming, tool dispatch, multi-round loops
- **Middleware pipeline**: compaction, post-turn memory, and tool-storm detection plug into every agent turn
- Built-in tools: `file_list`, `file_read`, `file_write`, `file_edit`, `apply_patch`, `grep_files`, `glob_files`, `execute_command`
- **Sub-agent delegation** (`sessions_spawn` / `sessions_send` / `sessions_list` / `sessions_history` / `sessions_pending_completions` / `task_output`) with configurable nesting depth and background execution
- **Long-term memory**: search, get, and consolidating memory via `ILongTermMemory` with periodic flush
- **Knowledge RAG**: SQLite vector store with OpenAI-compatible embeddings, document ingestion (PDF, text, Markdown), and turn-scoped knowledge search

### Context Management
- **Dynamic compaction**: budget-aware multi-level compaction (normal → elevated → high → critical → overflow)
- **3-level pass**: truncate args → conversation compact + summarize → tool result eviction
- **Send-boundary hygiene**: compact oversized tool payloads in every outbound request
- **Tool storm breaker**: detects runaway tool loops and halts the agent to prevent infinite cycles
- **Semantic cutoff planner**: intelligently chooses compaction boundaries based on message score

### Automation & Integration
- **Scheduled tasks** — daily, interval, one-shot, or manual; per-task workspace & prompt; keep-awake support
- **Skills** — YAML + Handlebars templates in `~/.athlon-agent/skills/`; XML prompt rendering
- **MCP** — full Model Context Protocol support: server configuration UI, stdio & streamable HTTP transports, automatic tool search (direct/search/auto modes with configurable thresholds)
- **Behavior Reporting** — opt-in event telemetry (disabled by default); batched HTTP upload for usage analytics

### Safety & Ops
- API keys encrypted with Windows DPAPI (not plain JSON)
- Tool approval system: configurable allow/deny lists for commands, file-scope policies
- JSONL audit logs for tool calls and HTTP interactions
- **Training Data Flywheel** — auto-extracts SFT/DPO training samples from real agent interactions (`CorrectionDetector` → `TurnTrajectoryExtractor` → `TrainingSampleStore`)
- GitHub Actions CI + tag-based releases

---

## 📸 Screenshots

> Add screenshots to `docs/images/` and embed them here — PRs welcome!

<p align="center">
  <img src="src/Athlon.Agent.App/Assets/app-icon-128.png" alt="Athlon Agent" width="128" />
  <br />
  <sub>Native WPF shell · dual themes · scheduled tasks · workspace editor</sub>
</p>

---

## ⚡ Quick Start

### Prerequisites

- Windows 10/11 x64
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)

**WebView2 (chat UI):**

- **Release installers** bundle a Fixed Version WebView2 Runtime for Windows 10.
- On **Windows 11**, the app uses the default WebView2 initialization path (same as v2.3.1), relying on the system Evergreen runtime.
- On **Windows 10**, the app tries the bundled Fixed Version first, then falls back to default Evergreen initialization if needed.
- To test the same bundled runtime locally before release:

```powershell
pwsh tools/fetch-webview2-fixed-runtime.ps1
dotnet run --project src/Athlon.Agent.App/Athlon.Agent.App.csproj
```

### Run from source

```powershell
git clone https://github.com/karsonto/athlon-work.git
cd athlon-work
dotnet run --project src/Athlon.Agent.App/Athlon.Agent.App.csproj
```

### First launch

1. Open **Settings** and set your OpenAI-compatible **endpoint**, **model**, and **API key**.
2. Add a **workspace** folder the agent can read and edit.
3. Start chatting — the agent will use tools to explore files on demand.

### Debug builds (skip license gate)

```powershell
$env:ATHLON_SKIP_LICENSE = "1"
dotnet run --project src/Athlon.Agent.App/Athlon.Agent.App.csproj
```

---

## 🏗 Architecture

```mermaid
flowchart TB
    subgraph UI["Athlon.Agent.App (WPF)"]
        MW[MainWindow shell]
        ShellVm[MainShellViewModel]
        Pages[Chat / Settings / Knowledge / Schedule pages]
        MD[MarkdownMessageView]
        SCH[SchedulerService]
    end

    subgraph Core["Athlon.Agent.Core"]
        RT[AgentRuntime]
        CMP[Context Compaction Pipeline]
        MEM[Long-Term Memory]
        MID[Turn Middleware Pipeline]
        TRAIN[Training Data Flywheel]
    end

    subgraph Infra["Athlon.Agent.Infrastructure"]
        LLM[OpenAI-compatible client]
        TOOLS[Filesystem & agent tools]
        STORE[File storage + DPAPI]
        KNOW[Knowledge RAG + Embeddings]
        SUB[Sub-Agent Engine]
        BEH[Behavior Report]
    end

    subgraph Ext["Extensions"]
        SK[Skills (YAML + Handlebars)]
        MCP[MCP Client + Tool Search]
    end

    MW --> ShellVm
    ShellVm --> Pages
    Pages --> RT
    SCH --> RT
    RT --> MID
    MID --> CMP
    MID --> MEM
    RT --> SUB
    RT --> TRAIN
    RT --> LLM
    RT --> TOOLS
    RT --> SK
    RT --> MCP
    RT --> KNOW
    ShellVm --> STORE
```

`MainWindow.xaml` is a thin shell (~300 lines): navigation sidebar, lazy-loaded page host (`PageViewFactory`), context sidebar, and status chrome. Page markup lives in `Views/*PageView.xaml`; composer/chat logic is in `ChatPageViewModel`, settings credentials in `SettingsViewModel`. Startup milestones are traced via `App.StartupTrace` (written to `startup.log` under the app logs folder).

```
src/
  Athlon.Agent.App/             WPF UI, view models, scheduler, themes
  Athlon.Agent.Core/            Agent runtime, compaction, memory, middleware, training data
  Athlon.Agent.Infrastructure/  LLM client, tools, knowledge RAG, licensing, SSO, sub-agents
  Athlon.Agent.Mcp/             MCP client foundation (ModelContextProtocol.Core)
  Athlon.Agent.Skills/          Skill loading and Handlebars rendering
tests/
  Athlon.Agent.Tests/           xUnit tests (140+ test files)
```

### Key Dependencies

| Package | Version |
|---------|---------|
| .NET SDK | 10.0 |
| CommunityToolkit.Mvvm | 8.4.2 |
| AvalonEdit | 6.3.0.90 |
| MdXaml | 1.27.0 |
| Markdig | 0.40.0 |
| Microsoft.Web.WebView2 | 1.0.2903.40 |
| ModelContextProtocol.Core | 1.3.0 |
| Microsoft.Data.Sqlite | 10.0.9 |
| Serilog | 4.3.1 |
| Velopack | 0.0.1298 |
| UglyToad.PdfPig | 1.7.0-custom-5 |
| YamlDotNet | 18.0.0 |

---

## 🛠 Build & Test

```powershell
dotnet build Athlon.Agent.slnx
dotnet test Athlon.Agent.slnx
```

If the app is running and locks output files:

```powershell
dotnet build src/Athlon.Agent.App/Athlon.Agent.App.csproj -p:OutDir=.\artifacts\verify\out\
```

### Release packaging (Velopack)

```powershell
dotnet tool install -g vpk --version 0.0.1298
.\build.bat 1.0.0
```

Outputs under `Releases/`: `AthlonAgent-Setup.exe`, portable zip, and update nupkg. See [Auto-Update](#auto-update-intranet) for intranet deployment.

---

## ⚙️ Configuration

Runtime data lives under `%USERPROFILE%\.athlon-agent\`:

```text
.athlon-agent/
  config/        settings.json, license.lic
  sessions/      conversation history (JSONL + Markdown + transcripts)
  skills/        SKILL.md folders
  logs/          Serilog logs + startup.log
  credentials/   DPAPI-encrypted API keys
  audit/         tool-call audit JSONL
  training-data/ SFT + DPO training data (JSONL, opt-in)
  behavior/      Behavior report pending events (JSONL, opt-in)
```

### Model settings (in-app or `config/settings.json`)

| Setting | Description |
|---------|-------------|
| Endpoint | OpenAI-compatible base URL |
| Model | Chat model identifier |
| API key | Stored with DPAPI locally |
| Max tokens | Optional; empty = API default |

### Built-in tools

| Tool | Purpose |
|------|---------|
| `file_list` / `glob_files` / `grep_files` | Discover and search workspace |
| `file_read` | Stream-read with line limits and offset |
| `file_write` / `file_edit` / `apply_patch` | Create or patch files (with backup) |
| `execute_command` | Shell via `cmd.exe /c` (allow/deny lists + approval) |
| `memory_search` / `memory_get` | Long-term memory retrieval |
| `knowledge_search` | RAG search across ingested documents |
| `load_skill_through_path` | Load skill instructions at runtime |
| `sessions_spawn` / `sessions_send` / … | Sub-agent delegation (when enabled) |
| `todo_write` | Task plan management |
| MCP tools | Dynamic tool discovery via configured MCP servers |

Details: workspace guard, timeouts, compaction, and tool permission policies → [Context compaction](docs/features/context-compaction.md).

### Agent turn timeout

```json
{
  "AgentTurn": {
    "TimeoutMinutes": 120
  }
}
```

`0` = disabled (only manual Stop ends the run). Range: 1–180 minutes.

---

## 📦 Auto-Update (Intranet)

The client checks an **internal HTTP update server** (not GitHub directly):

1. Sync `Releases/` from a GitHub Release to e.g. `https://update.corp.local/athlon-agent/`.
2. Configure `config/settings.json`:

```json
{
  "Update": {
    "Enabled": true,
    "BaseUrl": "https://update.corp.local/athlon-agent"
  }
}
```

Or set `ATHLON_UPDATE_URL`. Push a tag to trigger CI release:

```powershell
git tag v1.0.0
git push origin v1.0.0
```

---

## 📚 Documentation

| Doc | Description |
|-----|-------------|
| [Theme & UI conventions](docs/development/theme-and-ui-conventions.md) | Color tokens, theme switch rules (for contributors & AI) |
| [Context compaction](docs/features/context-compaction.md) | Dynamic compaction, hygiene, eviction |
| [Training data flywheel](docs/development/training-data-flywheel.md) | Auto-extracting SFT/DPO training data from agent interactions |
| [Behavior report events](docs/features/behavior-report-events.md) | Event collection and upload specification |
| [Localization conventions](docs/development/localization-conventions.md) | Adding new languages, string keys, UI patterns |
| [License tooling](tools/license/README.md) | RSA license generation for enterprise deployments |

---

## 🤝 Contributing

Contributions are welcome — whether it's a bug fix, a new tool, UI polish, docs, or tests.

**Start here:** [CONTRIBUTING.md](CONTRIBUTING.md) — setup, architecture rules, PR checklist, and commit style.

Quick summary:

1. **Fork** the repo and create a branch from `main`
2. **Follow** existing MVVM / service patterns — keep model logic out of WPF views
3. **Read** [theme & UI conventions](docs/development/theme-and-ui-conventions.md) before UI changes
4. **Run** `dotnet build` and `dotnet test` before opening a PR
5. **Keep** persistence file-based via `IAppPathProvider` (no hardcoded `%LocalAppData%`)

### Good first issues

- Add tests for `AppPathProvider`, workspace guard, filesystem tools
- MCP server lifecycle: connect, `tools/list`, `tools/call`, status UI
- Command execution confirmation dialog
- Session branch management
- Screenshots for the README

### Notes for AI-assisted development

- Extend `AgentRuntime`, `AgentEnvironmentPromptBuilder`, and tools — not the WPF layer
- UI logic → `Athlon.Agent.App/ViewModels/`
- Theme colors → palette tokens only; subscribe `ThemeChanged` when caching brushes
- `.pen` design files → Pencil MCP tools only
- **Training data collection** — every tool call, error, and user correction flows through `CorrectionDetector` and produces SFT/DPO samples automatically. When extending the agent loop:
  - Add new `CorrectionDetector.Detect*()` methods for new trajectory types
  - Wire extraction into `TurnTrajectoryExtractor.Extract*Samples()`
  - Register in `TrainingSampleStore.RecordTurnAsync()` (see [training data flywheel](docs/development/training-data-flywheel.md))

---

## 🔐 License

Athlon Agent ships with **AD-account-bound license validation** for enterprise deployments. Each license is signed (RSA-2048) and tied to a Windows domain account.

| Audience | How to run |
|----------|------------|
| **Developers** | Debug build + `ATHLON_SKIP_LICENSE=1` |
| **Enterprise** | Issue `.lic` via [`tools/license/`](tools/license/README.md) |

License lookup order:

1. `license.lic` next to `Athlon.Agent.App.exe`
2. `%USERPROFILE%\.athlon-agent\config\license.lic`

This is offline signature validation for internal compliance — not DRM. The **source code is open** for inspection, learning, and contribution; production use in licensed environments requires a valid license file.

---

## 🗺 Roadmap

- [ ] Full MCP server lifecycle UI (connect, list tools, call, reconnect, status indicators)
- [ ] Command execution confirmation dialog
- [ ] Session branching
- [ ] Richer code-block actions (diff, run)
- [ ] Optional code signing in release pipeline
- [ ] README screenshots & demo GIF

---

## ⭐ Star History

If you find Athlon Agent useful, **star the repo** to support the project and help other developers discover it.

---

<p align="center">
  <sub>Built with .NET 10 · WPF · CommunityToolkit.Mvvm · Serilog · MdXaml · Velopack · SQLite</sub>
</p>
