using System.IO;
using System.Text;
using Athlon.Agent.Core;
using Athlon.Agent.Infrastructure;
using Athlon.Agent.Skills;

namespace Athlon.Agent.App.Services.Room;

internal sealed class RoomDockerApplyPatchTool(IRoomSandboxBackend sandbox, WorkspaceGuard guard)
    : IAgentTool, ILocalWorkspaceTool
{
    public ToolDefinition Definition { get; } = new(
        "apply_patch",
        ToolDescriptions.ApplyPatch,
        ToolSchema.Object()
            .String("patch", "V4A patch (*** Begin Patch) or a unified diff", required: true)
            .String("path", "Workspace-relative path; when set, only hunks for this file are applied")
            .Build(),
        RequiresApproval: true);

    public async Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken cancellationToken = default)
    {
        if (!ToolArguments.TryGetRequired(invocation, "patch", out var patch, out var error))
        {
            return error;
        }

        if (!UnifiedDiffParser.TryParse(patch, out var files, out var parseError))
        {
            return ToolResult.Failure("Invalid patch", parseError ?? "Could not parse patch.");
        }

        string? pathFilter = null;
        if (invocation.Arguments.TryGetString("path", out var filterPath) && !string.IsNullOrWhiteSpace(filterPath))
        {
            pathFilter = guard.Normalize(filterPath.Trim());
        }

        var roomId = RoomDockerToolFactory.RequireRoomId();
        var map = RoomAgentTurnScope.Current?.Options.PathMap
            ?? throw new InvalidOperationException("Sandbox path map missing.");
        var applied = new List<string>();

        foreach (var file in files)
        {
            var hostPath = guard.Normalize(file.Path);
            if (pathFilter is not null
                && !string.Equals(hostPath, pathFilter, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var containerPath = map.ToContainer(hostPath);
            if (containerPath is null)
            {
                return ToolResult.Failure("Outside sandbox mounts", hostPath);
            }

            var existingBytes = await sandbox.ReadFileAsync(roomId, containerPath, cancellationToken)
                .ConfigureAwait(false);
            var original = existingBytes is null ? string.Empty : Encoding.UTF8.GetString(existingBytes);
            var applyResult = PatchApplier.Apply(original, file);
            if (!applyResult.Succeeded)
            {
                return ToolResult.Failure("Patch apply failed", $"{hostPath}: {applyResult.Error}");
            }

            if (applyResult.Status == PatchApplyStatus.Unchanged)
            {
                continue;
            }

            var updated = TextLineEndings.ToDiskText(original, applyResult.Text);
            await sandbox.WriteFileAsync(roomId, containerPath, Encoding.UTF8.GetBytes(updated), cancellationToken)
                .ConfigureAwait(false);
            applied.Add(RoomDockerToolFactory.HostDisplay(hostPath));
        }

        return applied.Count == 0
            ? ToolResult.Success("No changes applied", "Patch produced no file changes.")
            : ToolResult.Success($"Applied patch to {applied.Count} file(s)", string.Join("\n", applied));
    }
}

internal sealed class RoomDockerGrepFilesTool(IRoomSandboxBackend sandbox, WorkspaceGuard guard)
    : IAgentTool, IParallelizableAgentTool, ILocalWorkspaceTool
{
    private const int MaxMatches = 200;

    public ToolDefinition Definition { get; } = new(
        "grep_files",
        ToolDescriptions.GrepFiles(2000, MaxMatches),
        ToolSchema.Object()
            .String("pattern", "Text pattern (literal or regex).", required: true, minLength: 1)
            .String("path", ToolPathDescriptions.OptionalReadDirectory)
            .String("glob", "File glob filter, e.g. *.cs", defaultValue: "*", minLength: 1)
            .Boolean("regex", "Treat pattern as regex", defaultValue: false)
            .Build());

    public async Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken cancellationToken = default)
    {
        if (!ToolArguments.TryGetRequired(invocation, "pattern", out var pattern, out var error))
        {
            return error;
        }

        if (!RoomDockerToolFactory.TryResolveOptionalContainerPath(
                invocation, guard, out var hostPath, out var containerPath, out error,
                requireInsideWorkspace: false))
        {
            return error;
        }

        var useRegex = invocation.Arguments.GetBoolean("regex");
        var glob = invocation.Arguments.GetString("glob") ?? "*";
        var roomId = RoomDockerToolFactory.RequireRoomId();

        // Prefer ripgrep-like via grep in container
        var grepFlags = useRegex ? "-RIn" : "-RInF";
        var include = glob is "*" or "*.*" ? string.Empty : $" --include={ShellQuote(glob)}";
        var cmd =
            $"grep {grepFlags}{include} -- {ShellQuote(pattern)} {ShellQuote(containerPath)} 2>/dev/null | head -n {MaxMatches}";
        var result = await sandbox.ExecAsync(roomId, cmd, "/", 60, cancellationToken).ConfigureAwait(false);
        // grep exit 1 = no matches
        if (result.ExitCode > 1)
        {
            return ToolResult.Failure("grep failed", result.Stderr);
        }

        var map = RoomAgentTurnScope.Current?.Options.PathMap;
        var lines = new List<string>();
        foreach (var line in result.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (map is null)
            {
                lines.Add(line);
                continue;
            }

            // containerPath:line:content → hostPath:line:content
            var colon = line.IndexOf(':');
            if (colon <= 0)
            {
                lines.Add(line);
                continue;
            }

            var cPath = line[..colon];
            var rest = line[colon..];
            var host = map.ToHost(cPath) ?? cPath;
            lines.Add(RoomDockerToolFactory.HostDisplay(host) + rest);
        }

        return lines.Count == 0
            ? ToolResult.Success("No matches", "No matches found")
            : ToolResult.Success($"Found {lines.Count} matches", string.Join("\n", lines));
    }

    private static string ShellQuote(string value) =>
        "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
}

internal sealed class RoomDockerGlobFilesTool(IRoomSandboxBackend sandbox, WorkspaceGuard guard)
    : IAgentTool, IParallelizableAgentTool, ILocalWorkspaceTool
{
    public ToolDefinition Definition { get; } = new(
        "glob_files",
        ToolDescriptions.GlobFiles(200),
        ToolSchema.Object()
            .String("pattern", "Glob pattern (supports **)", required: true, minLength: 1)
            .String("path", ToolPathDescriptions.OptionalReadDirectory)
            .Build());

    public async Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken cancellationToken = default)
    {
        if (!ToolArguments.TryGetRequired(invocation, "pattern", out var pattern, out var error))
        {
            return error;
        }

        if (!RoomDockerToolFactory.TryResolveOptionalContainerPath(
                invocation, guard, out var hostRoot, out var containerRoot, out error,
                requireInsideWorkspace: false))
        {
            return error;
        }

        var roomId = RoomDockerToolFactory.RequireRoomId();
        if (!await sandbox.IsDirectoryAsync(roomId, containerRoot, cancellationToken).ConfigureAwait(false))
        {
            return ToolResult.Failure("Directory not found", RoomDockerToolFactory.HostDisplay(hostRoot));
        }

        // Use find + bash; convert simple globs. For ** use find -path
        var findPattern = pattern.Replace("**/", "", StringComparison.Ordinal).Replace("**", "*", StringComparison.Ordinal);
        var cmd =
            $"cd {ShellQuote(containerRoot)} && find . -type f -name {ShellQuote(findPattern)} 2>/dev/null | head -n 200";
        if (pattern.Contains("**", StringComparison.Ordinal))
        {
            var pathGlob = pattern.StartsWith("**/", StringComparison.Ordinal)
                ? "./" + pattern["**/".Length..]
                : "./" + pattern;
            pathGlob = pathGlob.Replace("**", "*", StringComparison.Ordinal);
            cmd =
                $"cd {ShellQuote(containerRoot)} && find . -type f -path {ShellQuote(pathGlob)} 2>/dev/null | head -n 200";
        }

        var result = await sandbox.ExecAsync(roomId, cmd, containerRoot, 60, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0 && string.IsNullOrWhiteSpace(result.Stdout))
        {
            return ToolResult.Failure("glob failed", result.Stderr);
        }

        var map = RoomAgentTurnScope.Current?.Options.PathMap;
        var lines = result.Stdout
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line =>
            {
                var rel = line.StartsWith("./", StringComparison.Ordinal) ? line[2..] : line.TrimStart('.');
                var cPath = containerRoot.TrimEnd('/') + "/" + rel.TrimStart('/');
                var host = map?.ToHost(cPath) ?? Path.Combine(hostRoot, rel.Replace('/', Path.DirectorySeparatorChar));
                return RoomDockerToolFactory.HostDisplay(host);
            })
            .ToArray();

        return lines.Length == 0
            ? ToolResult.Success("No matching files found", "No matching files found")
            : ToolResult.Success($"Found {lines.Length} matching entries", string.Join("\n", lines));
    }

    private static string ShellQuote(string value) =>
        "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
}

internal sealed class RoomDockerExecuteCommandTool(
    IRoomSandboxBackend sandbox,
    WorkspaceGuard guard,
    AppSettings settings) : IAgentTool, ILocalWorkspaceTool
{
    public ToolDefinition Definition { get; } = new(
        "execute_command",
        ToolDescriptions.ExecuteCommandLocal(
            BackgroundCommandRegistry.DefaultBlockUntilMs,
            ExecuteCommandTool.DefaultTimeoutSeconds,
            ExecuteCommandTool.MaxTimeoutSeconds),
        ToolSchema.Object()
            .String("command", "Command line", required: true, minLength: 1)
            .String("cwd", ToolPathDescriptions.OptionalWorkspaceRelativeCwd)
            .Integer(
                "timeout",
                $"Hard stop in seconds (default {ExecuteCommandTool.DefaultTimeoutSeconds})",
                defaultValue: ExecuteCommandTool.DefaultTimeoutSeconds,
                minimum: 1,
                maximum: ExecuteCommandTool.MaxTimeoutSeconds)
            .Integer(
                "block_until_ms",
                "Ignored in sandbox (commands always run to completion or timeout).",
                defaultValue: BackgroundCommandRegistry.DefaultBlockUntilMs,
                minimum: 0)
            .Build(),
        RequiresApproval: true,
        Group: ToolGroup.Builtin,
        MaxOutputChars: ExecuteCommandTool.MaxCapturedOutputChars,
        InvocationPolicy: ToolInvocationPolicy.Ask);

    public async Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken cancellationToken = default)
    {
        if (!ToolArguments.TryGetRequired(invocation, "command", out var command, out var error))
        {
            return error;
        }

        if (CommandDenyListMatcher.IsDenied(command, settings.ToolPermissions.CommandDenyList))
        {
            return ToolResult.Failure("Command denied", command);
        }

        if (!ToolArguments.TryResolveWorkingDirectory(invocation, guard, out var hostCwd, out error))
        {
            return error;
        }

        var map = RoomAgentTurnScope.Current?.Options.PathMap;
        if (map is null)
        {
            return ToolResult.Failure("Sandbox path map missing", "Room sandbox path map is not configured.");
        }

        var containerCwd = map.ToContainer(hostCwd);
        if (containerCwd is null)
        {
            return ToolResult.Failure("Outside sandbox mounts", hostCwd);
        }

        var timeoutSeconds = Math.Clamp(
            ToolArguments.GetInt32(invocation, "timeout", ExecuteCommandTool.DefaultTimeoutSeconds),
            1,
            ExecuteCommandTool.MaxTimeoutSeconds);

        var roomId = RoomDockerToolFactory.RequireRoomId();
        var result = await sandbox
            .ExecAsync(roomId, command, containerCwd, timeoutSeconds, cancellationToken)
            .ConfigureAwait(false);

        var output = new StringBuilder();
        if (!string.IsNullOrEmpty(result.Stdout))
        {
            output.Append(result.Stdout);
        }

        if (!string.IsNullOrEmpty(result.Stderr))
        {
            if (output.Length > 0)
            {
                output.AppendLine();
            }

            output.Append(result.Stderr);
        }

        var text = output.ToString();
        if (text.Length > ExecuteCommandTool.MaxCapturedOutputChars)
        {
            text = text[..ExecuteCommandTool.MaxCapturedOutputChars] + "\n…(truncated)";
        }

        var summary = result.ExitCode == 0
            ? "Command completed"
            : $"Command exited with code {result.ExitCode}";
        return result.ExitCode == 0
            ? ToolResult.Success(summary, text)
            : ToolResult.Failure(summary, text);
    }
}

internal sealed class RoomDockerLoadSkillTool(
    IRoomSandboxBackend sandbox,
    ISkillRuntime skillRuntime,
    IAgentSkillCatalog skillCatalog,
    IAppPathProvider paths) : IAgentTool
{
    private const string SkillMarkdownPath = "SKILL.md";

    public ToolDefinition Definition { get; } = new(
        "load_skill_through_path",
        "Load and activate a skill resource by skill name and resource path. "
        + "Use path=\"SKILL.md\" for instructions or an exact skill-internal relative resource path. "
        + "Do not use '.', './', directories, or absolute paths. Available skills are listed once in the skill catalog prompt.",
        ToolSchema.Object()
            .String("skillId", "Skill name from the available skill catalog.", required: true, minLength: 1)
            .String("path", "Relative resource path within the skill. Use 'SKILL.md' for full instructions.", required: true, minLength: 1)
            .Build());

    public async Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken cancellationToken = default)
    {
        if (!invocation.Arguments.TryGetString("skillId", out var skillId) || string.IsNullOrWhiteSpace(skillId))
        {
            return ToolResult.Failure("Missing skillId", "Missing or empty required parameter: skillId");
        }

        if (!invocation.Arguments.TryGetString("path", out var path) || string.IsNullOrWhiteSpace(path))
        {
            return ToolResult.Failure("Missing path", "Missing or empty required parameter: path");
        }

        skillId = skillId.Trim();
        string normalizedPath;
        try
        {
            normalizedPath = NormalizeResourcePath(path.Trim());
        }
        catch (ArgumentException ex)
        {
            return ToolResult.Failure("Skill load failed", ex.Message);
        }

        path = normalizedPath;

        AgentSkill? skill;
        try
        {
            // Catalog / allow-list validation on host (does not read resource body from host disk for return)
            skill = skillCatalog.GetSkillById(skillId);
            if (skill is null)
            {
                return ToolResult.Failure("Skill load failed", $"Skill not found: '{skillId}'.");
            }

            // Trigger activation / enablement checks via runtime without trusting host content for files-root
            _ = skillRuntime.GetSkills();
            skillRuntime.Activate(skillId);
        }
        catch (ArgumentException ex)
        {
            return ToolResult.Failure("Skill load failed", ex.Message);
        }

        var map = RoomAgentTurnScope.Current?.Options.PathMap;
        if (map is null)
        {
            return ToolResult.Failure("Sandbox path map missing", "Room sandbox path map is not configured.");
        }

        var hostSkillDir = skill.SkillDirectory;
        if (string.IsNullOrWhiteSpace(hostSkillDir))
        {
            return ToolResult.Failure("Skill load failed", $"Skill '{skillId}' has no files directory.");
        }

        var containerSkillDir = map.ToContainer(Path.GetFullPath(hostSkillDir));
        if (containerSkillDir is null)
        {
            // Skill may live under paths.SkillsPath — ensure map covers it
            containerSkillDir = map.ToContainer(Path.GetFullPath(hostSkillDir));
            if (containerSkillDir is null)
            {
                return ToolResult.Failure(
                    "Skill outside sandbox mounts",
                    $"Skill directory is not under mounted skills root ({paths.SkillsPath}).");
            }
        }

        var containerResource = containerSkillDir.TrimEnd('/') + "/" + path.Replace('\\', '/');
        var roomId = RoomDockerToolFactory.RequireRoomId();
        var bytes = await sandbox.ReadFileAsync(roomId, containerResource, cancellationToken).ConfigureAwait(false);
        if (bytes is null)
        {
            return ToolResult.Failure(
                "Skill load failed",
                $"Resource not found: '{path}' in skill '{skillId}' (container path {containerResource}).");
        }

        var content = Encoding.UTF8.GetString(bytes);
        var sb = new StringBuilder();
        if (string.Equals(path, SkillMarkdownPath, StringComparison.Ordinal))
        {
            sb.AppendLine($"Successfully loaded skill: {skillId}");
            sb.AppendLine();
            sb.AppendLine($"Name: {skill.Name}");
            sb.AppendLine($"Description: {skill.Description}");
            sb.AppendLine($"Files root: {containerSkillDir}");
            sb.AppendLine();
            sb.AppendLine("Content:");
            sb.AppendLine("---");
            sb.AppendLine(content);
            sb.AppendLine("---");
        }
        else
        {
            sb.AppendLine($"Successfully loaded resource from skill: {skillId}");
            sb.AppendLine($"Resource path: {path}");
            sb.AppendLine($"Files root: {containerSkillDir}");
            sb.AppendLine();
            sb.AppendLine("Content:");
            sb.AppendLine("---");
            sb.AppendLine(content);
            sb.AppendLine("---");
        }

        return ToolResult.Success($"Loaded skill resource from {skillId}", sb.ToString());
    }

    private static string NormalizeResourcePath(string path)
    {
        var trimmed = path.Trim().Replace('\\', '/');
        if (trimmed is "." or "./" or ".." or "/"
            || trimmed.StartsWith("./", StringComparison.Ordinal)
            || trimmed.StartsWith("../", StringComparison.Ordinal)
            || Path.IsPathRooted(trimmed))
        {
            throw new ArgumentException(
                "Invalid path: use a relative resource path such as 'SKILL.md' or 'references/guide.md'.");
        }

        return trimmed;
    }
}
