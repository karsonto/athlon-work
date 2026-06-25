# Contributing to Athlon Agent

Thank you for your interest in contributing! Athlon Agent is a native Windows AI coding agent built with .NET 10 and WPF. Every bug report, doc improvement, test, and pull request helps.

## Table of Contents

- [Code of Conduct](#code-of-conduct)
- [Ways to Contribute](#ways-to-contribute)
- [Development Setup](#development-setup)
- [Project Layout](#project-layout)
- [Architecture Rules](#architecture-rules)
- [Coding Conventions](#coding-conventions)
- [UI & Theme Changes](#ui--theme-changes)
- [Testing](#testing)
- [Commit Messages](#commit-messages)
- [Pull Request Process](#pull-request-process)
- [AI-Assisted Development](#ai-assisted-development)
- [Security & Secrets](#security--secrets)
- [License Note](#license-note)

---

## Code of Conduct

Be respectful and constructive. Focus on technical merit, keep discussions on-topic, and assume good intent. Harassment and personal attacks are not tolerated.

---

## Ways to Contribute

You do not need to write code to help:

| Type | Examples |
|------|----------|
| **Bug reports** | Crashes, theme glitches, tool failures — include steps to reproduce |
| **Feature ideas** | Open an issue first for large changes |
| **Code** | Bug fixes, tools, UI polish, tests |
| **Docs** | README, `docs/`, inline comments for non-obvious logic |
| **Screenshots** | Add images under `docs/images/` for the README |

**Good first issues** (from our roadmap):

- Tests for `AppPathProvider`, workspace guard, filesystem tools
- MCP server lifecycle (connect, `tools/list`, `tools/call`)
- Command execution confirmation UI
- Session branch management
- README screenshots and demo GIF

---

## Development Setup

### Requirements

- **Windows 10/11** (WPF target)
- **[.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)**

### Clone and run

```powershell
git clone https://github.com/karsonto/athlon-work.git
cd athlon-work

# Skip AD license gate in Debug (see License Note below)
$env:ATHLON_SKIP_LICENSE = "1"

dotnet run --project src/Athlon.Agent.App/Athlon.Agent.App.csproj
```

### Build and test

```powershell
dotnet build Athlon.Agent.slnx
dotnet test Athlon.Agent.slnx
```

### Bundled WebView2 Fixed Runtime

Release builds download and package a Fixed Version WebView2 Runtime for Windows 10 compatibility.

| File | Purpose |
|------|---------|
| `tools/webview2-runtime.version` | Pinned runtime version (must match a build on the [WebView2 download page](https://developer.microsoft.com/microsoft-edge/webview2/)) |
| `tools/webview2-runtime.download-url` | Pinned CAB CDN URL used when page scraping fails (update together with version) |
| `tools/fetch-webview2-fixed-runtime.ps1` | Downloads, expands, and copies runtime to `src/Athlon.Agent.App/runtimes/webview2/x64/` |

To refresh the bundled runtime locally or before a manual publish:

```powershell
pwsh tools/fetch-webview2-fixed-runtime.ps1
```

Release CI runs this script automatically before `dotnet publish`. The app tries the bundled Fixed Version runtime first, then falls back to the system Evergreen WebView2 Runtime if bundled initialization fails (common on Windows 11).

If you update `tools/webview2-runtime.version`, run the fetch script and verify chat rendering, Mermaid preview, and HTML preview on a machine without a separate WebView2 install.

If the app is running and locks output DLLs:

```powershell
dotnet build src/Athlon.Agent.App/Athlon.Agent.App.csproj -p:OutDir=.\artifacts\verify\out\
```

### Local data

Runtime files are stored under `%USERPROFILE%\.athlon-agent\` (settings, sessions, logs, credentials). Do not commit this folder or any API keys from it.

---

## Project Layout

```text
src/
  Athlon.Agent.App/             WPF UI, ViewModels, themes, scheduler
  Athlon.Agent.Core/            Agent runtime, settings, domain models
  Athlon.Agent.Infrastructure/  LLM client, tools, storage, licensing
  Athlon.Agent.Mcp/             MCP client foundation
  Athlon.Agent.Skills/          Skill loading and templates
tests/
  Athlon.Agent.Tests/           xUnit tests
docs/
  development/                  Contributor guides (e.g. theme conventions)
  features/                     Feature deep-dives
.github/workflows/              CI and release automation
```

### Where to put new code

| Change | Location |
|--------|----------|
| UI / windows / controls | `Athlon.Agent.App/` |
| ViewModels | `Athlon.Agent.App/ViewModels/` |
| Agent loop, prompts, settings models | `Athlon.Agent.Core/` |
| Tools, HTTP, file I/O, DPAPI | `Athlon.Agent.Infrastructure/` |
| MCP protocol | `Athlon.Agent.Mcp/` |
| Skill format / rendering | `Athlon.Agent.Skills/` |
| Unit tests | `tests/Athlon.Agent.Tests/` |

**Do not** put model-calling or tool logic in WPF code-behind or ViewModels beyond UI orchestration.

---

## Architecture Rules

1. **Extend the runtime, not the UI** — New agent behavior belongs in `AgentRuntime`, `AgentEnvironmentPromptBuilder`, and tool classes.
2. **MVVM** — Use `CommunityToolkit.Mvvm` patterns already in the project. Keep views thin.
3. **File-first persistence** — Use `IFileStorageService`, `IAppPathProvider`, and existing JSON/JSONL patterns. No database unless there is a strong product reason.
4. **Workspace safety** — File tools must respect `WorkspaceGuard`. Writes/edits go through `AtomicFile` with backups.
5. **No hardcoded paths** — Use `IAppPathProvider` (folder: `.athlon-agent` under the user profile). Do not use `%LocalAppData%` or `AthlonAgent` for default data paths.
6. **Minimal diffs** — Match surrounding style. Avoid drive-by refactors in the same PR as a feature fix.

---

## Coding Conventions

- **C#** — Follow existing naming, nullable reference types, and `sealed`/`partial` patterns in the codebase.
- **XAML** — Prefer `DynamicResource Brush.*` for theme-aware colors. Use shared styles in `Themes/Controls.xaml`, `ChatStyles.xaml`, `Overlays.xaml`.
- **Comments** — Only for non-obvious business logic; code should be self-explanatory where possible.
- **Tests** — Add tests for real behavior (parsers, guards, compaction logic). Skip tests that only assert trivial getters.

---

## UI & Theme Changes

All color and theme logic must stay centralized. **Read before editing UI:**

📄 **[docs/development/theme-and-ui-conventions.md](docs/development/theme-and-ui-conventions.md)**

Quick rules:

- Define colors only in `DarkAppThemePalette` / `LightAppThemePalette` → `UiChromeColors` → `Brush.*` resources.
- Resolve brushes in code via `ThemeBrushResolver` or `ChatMessageToneColors` — never scatter `#RRGGBB` in ViewModels.
- Components that set `Foreground` / `Background` in code must handle `AppThemeManager.ThemeChanged` (or use `DynamicResource` in XAML).
- Markdown / FlowDocument: use `FlowDocumentMarkdownThemeFactory`; on theme switch, force document rebuild.
- Light and dark themes share **Indigo** (`#6366F1`) as the accent color.

After UI changes, manually verify **light ↔ dark** theme switching on chat messages (text, tables, code blocks, copy buttons).

---

## Testing

```powershell
# All tests
dotnet test tests/Athlon.Agent.Tests/Athlon.Agent.Tests.csproj

# Filtered (example)
dotnet test tests/Athlon.Agent.Tests --filter "FullyQualifiedName~FlowDocument"
```

CI (`.github/workflows/ci.yml`) runs **Debug and Release builds** on `windows-latest`. Run both locally when touching build-sensitive code (WPF, XAML, conditional compilation).

Before opening a PR:

- [ ] `dotnet build Athlon.Agent.slnx` succeeds
- [ ] `dotnet test Athlon.Agent.slnx` passes (if tests exist for your area)
- [ ] No new hardcoded secrets or license private keys
- [ ] UI changes checked in both themes (if applicable)

---

## Commit Messages

Use [Conventional Commits](https://www.conventionalcommits.org/) style, matching project history:

```text
feat: add scheduled task retry on failure
fix: resolve markdown table colors after theme switch
docs: update README quick start
refactor: extract FlowDocument theme factory
test: cover workspace guard path traversal
chore: bump Velopack tool version
```

- **feat** — new feature
- **fix** — bug fix
- **docs** — documentation only
- **refactor** — code change without behavior change
- **test** — tests only
- **chore** — tooling, build, misc

Write the subject in English, imperative mood, ≤ 72 characters when possible. Add a body for non-obvious context.

---

## Pull Request Process

1. **Fork** and create a branch from `main` (e.g. `fix/theme-table-colors`, `feat/mcp-connect`).
2. **Keep PRs focused** — one logical change per PR when possible.
3. **Describe** what changed and why. Include screenshots for UI changes (light + dark if relevant).
4. **Link issues** if applicable (`Fixes #123`).
5. **Ensure CI passes** — GitHub Actions must be green.
6. **Respond to review** — maintainers may request changes before merge.

### PR checklist (copy into description)

```markdown
## Summary
- ...

## Test plan
- [ ] Built locally (Debug + Release)
- [ ] Tests pass
- [ ] Manually tested: ...
- [ ] Theme switch verified (if UI)
```

---

## AI-Assisted Development

AI tools (Cursor, Copilot, etc.) are welcome if you review the output carefully.

Guidelines:

- Point AI at **[theme-and-ui-conventions.md](docs/development/theme-and-ui-conventions.md)** before UI work.
- Do not commit unreviewed bulk refactors or unrelated file changes.
- Verify build and behavior yourself — AI may miss WPF theme edge cases.
- For `.pen` design files, use Pencil MCP tools only (do not edit `.pen` as plain text).
- Prefer extending existing abstractions (`AgentRuntime`, `ThemeBrushResolver`, tools) over duplicating logic.

---

## Security & Secrets

**Never commit:**

- API keys, tokens, or passwords
- `tools/license/keys/private.pem` or generated `license.lic` for real accounts
- Contents of `~/.athlon-agent/credentials/`
- Customer data or session exports

Report security issues privately to the maintainers rather than opening a public issue, if the issue is sensitive.

---

## License Note

Source is open for learning and contribution. **Production deployments** may require an AD-bound license file (see [tools/license/README.md](tools/license/README.md)).

For local development:

```powershell
$env:ATHLON_SKIP_LICENSE = "1"   # Debug builds only
```

By contributing, you agree that your contributions are licensed under the same terms the project maintainers apply to the codebase. If you are unsure about licensing for your employer, check with your legal team before contributing.

---

## Questions?

- Open a [GitHub Issue](https://github.com/karsonto/athlon-work/issues) for bugs and feature discussion.
- Read the [README](README.md) for architecture overview and configuration.

Thank you for helping make Athlon Agent better!
