using System.Diagnostics;
using System.Text;
using System.Text.Json.Serialization;
using Athlon.Agent.Core.Compaction;

namespace Athlon.Agent.Core;

public sealed class AgentEnvironmentPromptBuilder(
    AppSettings settings,
    IAvailableSkillsProvider skillsProvider,
    IAgentHostEnvironment host) : IAgentEnvironmentPromptBuilder
{
    public string Build(AgentSession session, IReadOnlyList<ToolDefinition> tools)
    {
        var builder = new StringBuilder();
        var workspace = ResolveWorkspace(session);

        builder.AppendLine("You are Athlon Agent, a Windows desktop coding agent.");
        builder.AppendLine("Use the provided function tools when you need to inspect or modify workspace files. Do not guess file contents.");
        builder.AppendLine("Think through the user's goal, constraints, and risks before calling tools or making changes. Share concise reasoning when it helps the user follow your approach.");
        builder.AppendLine();
        AppendHostEnvironment(builder);
        builder.AppendLine();

        if (workspace is null || string.IsNullOrWhiteSpace(workspace.RootPath))
        {
            builder.AppendLine("当前工作区尚未设定。");
            builder.AppendLine("请让用户通过侧栏「配置」或设置页的 Workspace 指定工作区目录后，再使用 file_list、file_read、file_write、file_edit、grep_files、glob_files 等文件工具。");
            builder.AppendLine("在工作区未设定前，不要假设任何文件路径，也不要调用访问工作区文件的工具。");
        }
        else
        {
            builder.AppendLine("All relative file paths are resolved from the active workspace. Never access files outside the configured workspace.");
            builder.AppendLine("In file tool arguments (path), always use forward slashes (/), e.g. src/foo.cs — not backslashes (\\), even on Windows.");
            builder.AppendLine($"Active workspace: {workspace.Name}");
            builder.AppendLine($"Workspace root: {workspace.RootPath}");
            builder.AppendLine("Workspace contents are intentionally not embedded in this prompt because they change often.");
            builder.AppendLine("Use file_list to fetch a live directory listing when needed.");
            builder.AppendLine();
            AppendPlanningGuidance(builder);
        }

        builder.AppendLine();
        builder.AppendLine("Available native tools:");
        foreach (var tool in tools.Where(tool => !IsMcpTool(tool)))
        {
            builder.AppendLine($"- {tool.Name}: {tool.Description}");
        }

        builder.AppendLine();
        builder.AppendLine("Available MCP tools:");
        var mcpTools = tools.Where(IsMcpTool).ToArray();
        if (mcpTools.Length == 0)
        {
            builder.AppendLine("none (no enabled MCP servers with tools).");
        }
        else
        {
            foreach (var tool in mcpTools)
            {
                builder.AppendLine($"- {tool.Name}: {tool.Description}");
            }
        }

        var skills = skillsProvider.GetSkills();
        builder.AppendLine();
        if (skills.Count == 0)
        {
            builder.AppendLine($"Available skills: none installed under {host.SkillsDirectory}.");
            builder.AppendLine($"Install skills as <skill-name>/SKILL.md under {host.SkillsDirectory} with YAML frontmatter (name, description).");
        }
        else
        {
            builder.AppendLine($"Available skills (each folder under {host.SkillsDirectory}):");
            foreach (var skill in skills)
            {
                builder.AppendLine($"- {skill.Name}: {skill.Description} (skill-id: {skill.SkillId})");
            }

            builder.AppendLine("When a skill matches the task, follow instructions in its SKILL.md content.");
        }

        builder.AppendLine();
        builder.AppendLine("When answering questions about files, call file_read first if the content is needed.");
        builder.AppendLine("file_read returns lines as N|content for display only. For file_edit, old_text must be the exact on-disk substring without the N| prefix.");
        builder.AppendLine("When the user asks what files exist in the workspace or a directory, call file_list before answering.");
        builder.AppendLine("When searching file contents, call grep_files. When finding files by name or extension, call glob_files.");
        builder.AppendLine("For write operations, explain your intent before calling file_write or file_edit.");
        builder.AppendLine("For file_edit, copy old_text from the real file content (same indentation and line endings). Do not paste grep path:line: prefixes.");
        builder.AppendLine("Use execute_command when a shell command is needed to complete the task.");
        builder.AppendLine("On Windows, execute commands with cmd/cmd.exe semantics; do not use PowerShell syntax or PowerShell-specific commands.");
        builder.AppendLine("When context grows large, history is auto-compressed; full transcripts are kept under the session transcripts folder.");
        builder.AppendLine();
        AppendMermaidGuidance(builder);

        return builder.ToString();
    }

    private static void AppendMermaidGuidance(StringBuilder builder)
    {
        builder.AppendLine("Mermaid diagrams in chat:");
        builder.AppendLine("- When a diagram clarifies the answer better than prose alone, include one or more fenced ```mermaid code blocks (e.g. flowchart, sequenceDiagram, stateDiagram-v2, classDiagram, erDiagram, gantt).");
        builder.AppendLine("- Prefer Mermaid for: request/API flows, multi-step processes, component or deployment topology, state transitions, timelines, and decision branches.");
        builder.AppendLine("- Skip diagrams for simple factual answers, short lists, or when the user only wants code/text.");
        builder.AppendLine("- Keep each diagram focused; use multiple small diagrams instead of one huge chart.");
        builder.AppendLine("- In Athlon Agent the chat shows Mermaid as source code, not inline graphics. Tell the user they can right-click the message and choose \"查看 Mermaid 图表\" for an offline rendered preview.");
        builder.AppendLine("- Do not claim an inline image is visible unless you also describe the structure in text.");
    }

    private static void AppendPlanningGuidance(StringBuilder builder)
    {
        builder.AppendLine("Planning for multi-step or long-running tasks:");
        builder.AppendLine("- Before broad edits, use file_write to create or refresh plan.md at the workspace root.");
        builder.AppendLine("- plan.md should list ordered steps with clear status (e.g. [ ] / [x]), scope, and acceptance criteria.");
        builder.AppendLine("- Execute one step at a time according to plan.md; after each completed step, update plan.md promptly before starting the next.");
        builder.AppendLine("- If scope changes, revise plan.md first, then continue execution.");
    }

    private void AppendHostEnvironment(StringBuilder builder)
    {
        var now = DateTimeOffset.Now;
        var localZone = TimeZoneInfo.Local;

        builder.AppendLine("Host environment (current Windows user session):");
        builder.AppendLine($"- Current date/time (local): {now:yyyy-MM-dd HH:mm:ss} ({localZone.DisplayName})");
        builder.AppendLine($"- Current date/time (UTC): {now.UtcDateTime:yyyy-MM-dd HH:mm:ss}Z");
        builder.AppendLine($"- Platform: {(host.IsWindows ? "Windows" : "non-Windows")}");
        builder.AppendLine($"- OS: {host.OsDescription} ({host.OsVersion})");
        builder.AppendLine($"- User: {host.UserDomainName}\\{host.UserName}");
        builder.AppendLine($"- Machine: {host.MachineName}");
        builder.AppendLine($"- User profile: {host.UserProfilePath}");
        builder.AppendLine($"- Process current directory: {host.CurrentDirectory}");
        builder.AppendLine($"- System directory: {host.SystemDirectory}");
        builder.AppendLine($"- Architecture: process={host.ProcessArchitecture}, OS={host.OsArchitecture}, processors={host.ProcessorCount}");
        builder.AppendLine($"- Agent app data: {host.AppDataDirectory}");
        builder.AppendLine($"- Default skills directory: {host.SkillsDirectory}");
    }

    private WorkspaceSettings? ResolveWorkspace(AgentSession session)
    {
        if (!string.IsNullOrWhiteSpace(session.ActiveWorkspace))
        {
            var rootPath = Path.GetFullPath(session.ActiveWorkspace);
            return new WorkspaceSettings
            {
                Name = Path.GetFileName(rootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
                RootPath = rootPath
            };
        }

        return settings.Workspaces.FirstOrDefault(workspace => !string.IsNullOrWhiteSpace(workspace.RootPath));
    }

    private static bool IsMcpTool(ToolDefinition tool) =>
        string.Equals(tool.Source, "mcp", StringComparison.OrdinalIgnoreCase);

}
