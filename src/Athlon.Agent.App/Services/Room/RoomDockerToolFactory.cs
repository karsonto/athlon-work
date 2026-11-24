using System.IO;
using System.Text;
using Athlon.Agent.Core;
using Athlon.Agent.Infrastructure;
using Athlon.Agent.Skills;

namespace Athlon.Agent.App.Services.Room;

/// <summary>Builds sandbox-backed tools that share names with host local workspace tools.</summary>
public static class RoomDockerToolFactory
{
    public static readonly HashSet<string> ReplacedToolNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "file_list",
        "file_read",
        "file_write",
        "file_edit",
        "apply_patch",
        "grep_files",
        "glob_files",
        "execute_command",
        "command_await",
        "command_kill",
        "load_skill_through_path",
        "browser_navigate"
    };

    public static IReadOnlyList<IAgentTool> Create(
        IRoomDockerSandbox sandbox,
        WorkspaceGuard guard,
        AppSettings settings,
        ISkillRuntime skillRuntime,
        IAgentSkillCatalog skillCatalog,
        IAppPathProvider paths)
    {
        return
        [
            new RoomDockerFileListTool(sandbox, guard),
            new RoomDockerFileReadTool(sandbox, guard, settings),
            new RoomDockerFileWriteTool(sandbox, guard),
            new RoomDockerFileEditTool(sandbox, guard),
            new RoomDockerApplyPatchTool(sandbox, guard),
            new RoomDockerGrepFilesTool(sandbox, guard),
            new RoomDockerGlobFilesTool(sandbox, guard),
            new RoomDockerExecuteCommandTool(sandbox, guard, settings),
            new RoomDockerLoadSkillTool(sandbox, skillRuntime, skillCatalog, paths),
            new RoomDockerBrowserNavigateTool(sandbox)
        ];
    }

    internal static bool TryResolveContainerPath(
        ToolInvocation invocation,
        WorkspaceGuard guard,
        out string hostPath,
        out string containerPath,
        out ToolResult error,
        bool requireInsideWorkspace = true)
    {
        hostPath = string.Empty;
        containerPath = string.Empty;
        if (!WorkspaceToolHelper.TryResolveNormalizedPath(
                invocation,
                guard,
                out hostPath,
                out error,
                requireInsideWorkspace))
        {
            return false;
        }

        var map = RoomAgentTurnScope.Current?.Options.PathMap;
        if (map is null)
        {
            error = ToolResult.Failure("Sandbox path map missing", "Room sandbox path map is not configured.");
            return false;
        }

        var mapped = map.ToContainer(hostPath);
        if (mapped is null)
        {
            // Allow skills paths even when requireInsideWorkspace was false for file_read
            if (!requireInsideWorkspace)
            {
                mapped = map.ToContainer(hostPath);
            }
        }

        if (mapped is null)
        {
            // Skills cwd / files may be under skills root — WorkspaceGuard.Normalize may still produce host path
            error = ToolResult.Failure("Outside sandbox mounts", hostPath);
            return false;
        }

        containerPath = mapped;
        error = ToolResult.Success("OK");
        return true;
    }

    internal static bool TryResolveOptionalContainerPath(
        ToolInvocation invocation,
        WorkspaceGuard guard,
        out string hostPath,
        out string containerPath,
        out ToolResult error,
        string defaultPath = ".",
        bool requireInsideWorkspace = true)
    {
        hostPath = string.Empty;
        containerPath = string.Empty;
        if (!WorkspaceToolHelper.TryResolveOptionalNormalizedPath(
                invocation,
                guard,
                out hostPath,
                out error,
                defaultPath,
                requireInsideWorkspace))
        {
            return false;
        }

        var map = RoomAgentTurnScope.Current?.Options.PathMap;
        if (map is null)
        {
            error = ToolResult.Failure("Sandbox path map missing", "Room sandbox path map is not configured.");
            return false;
        }

        // execute_command cwd may be skills root
        var mapped = map.ToContainer(hostPath);
        if (mapped is null && guard.IsAllowedExecuteCommandWorkingDirectory(hostPath))
        {
            mapped = map.ToContainer(hostPath);
        }

        if (mapped is null)
        {
            error = ToolResult.Failure("Outside sandbox mounts", hostPath);
            return false;
        }

        containerPath = mapped;
        error = ToolResult.Success("OK");
        return true;
    }

    internal static string RequireRoomId()
    {
        var id = RoomAgentTurnScope.Current?.Options.RoomId;
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new InvalidOperationException("No active room agent turn.");
        }

        return id;
    }

    internal static string HostDisplay(string hostPath) => ToolPathNormalizer.ForModel(hostPath);
}

internal sealed class RoomDockerFileListTool(IRoomDockerSandbox sandbox, WorkspaceGuard guard)
    : IAgentTool, IParallelizableAgentTool, ILocalWorkspaceTool
{
    public ToolDefinition Definition { get; } = new(
        "file_list",
        "List files and directories (top-level only, up to 200 entries).",
        ToolSchema.Object()
            .String("path", ToolPathDescriptions.OptionalReadDirectory)
            .Build());

    public async Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken cancellationToken = default)
    {
        if (!RoomDockerToolFactory.TryResolveOptionalContainerPath(
                invocation, guard, out var hostPath, out var containerPath, out var error))
        {
            return error;
        }

        var roomId = RoomDockerToolFactory.RequireRoomId();
        if (!await sandbox.IsDirectoryAsync(roomId, containerPath, cancellationToken).ConfigureAwait(false))
        {
            return ToolResult.Failure("Not a directory", RoomDockerToolFactory.HostDisplay(hostPath));
        }

        var entries = await sandbox.ListDirectoryAsync(roomId, containerPath, cancellationToken).ConfigureAwait(false);
        var sb = new StringBuilder();
        foreach (var name in entries.OrderBy(static x => x, StringComparer.OrdinalIgnoreCase))
        {
            var child = containerPath.TrimEnd('/') + "/" + name;
            var isDir = await sandbox.IsDirectoryAsync(roomId, child, cancellationToken).ConfigureAwait(false);
            sb.Append(isDir ? "dir  " : "file ");
            sb.AppendLine(name);
        }

        return ToolResult.Success($"Listed {entries.Count} entries", sb.ToString());
    }
}

internal sealed class RoomDockerFileReadTool(
    IRoomDockerSandbox sandbox,
    WorkspaceGuard guard,
    AppSettings settings) : IAgentTool, IParallelizableAgentTool, ILocalWorkspaceTool
{
    public ToolDefinition Definition { get; } = new(
        "file_read",
        ToolDescriptions.FileRead,
        ToolSchema.Object()
            .String("path", "Absolute or workspace-relative file path to read.", required: true, minLength: 1)
            .Integer("start_line", "1-based start line (default 1)", defaultValue: 1, minimum: 1)
            .Integer("end_line", $"1-based end line, inclusive (default start_line + {FileReadSettingsDefaults.DefaultLineLimit - 1}; max {FileReadSettingsDefaults.MaxLinesPerCall} lines per call)", minimum: 1)
            .Build());

    public async Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken cancellationToken = default)
    {
        if (!RoomDockerToolFactory.TryResolveContainerPath(
                invocation, guard, out var hostPath, out var containerPath, out var error,
                requireInsideWorkspace: false))
        {
            return error;
        }

        var roomId = RoomDockerToolFactory.RequireRoomId();
        var bytes = await sandbox.ReadFileAsync(roomId, containerPath, cancellationToken).ConfigureAwait(false);
        if (bytes is null)
        {
            return ToolResult.Failure("File not found", RoomDockerToolFactory.HostDisplay(hostPath));
        }

        var fileRead = settings.FileRead;
        if (bytes.Length > fileRead.MaxFileBytes)
        {
            return ToolResult.Failure(
                "File too large",
                $"{RoomDockerToolFactory.HostDisplay(hostPath)} is {bytes.Length:N0} bytes (max {fileRead.MaxFileBytes:N0}).");
        }

        var text = Encoding.UTF8.GetString(bytes);
        var selection = FileReadLineReader.ResolveSelection(invocation, fileRead);
        var read = FileReadLineReader.ReadFromText(text, selection, fileRead);
        var summary = FileReadLineReader.BuildSummary(Path.GetFileName(hostPath), read);
        var content = read.LinesReturned == 0 && string.IsNullOrEmpty(read.Body)
            ? "(no lines in range)"
            : read.Body;
        return ToolResult.Success(summary, content);
    }
}

internal sealed class RoomDockerFileWriteTool(IRoomDockerSandbox sandbox, WorkspaceGuard guard)
    : IAgentTool, ILocalWorkspaceTool
{
    public ToolDefinition Definition { get; } = new(
        "file_write",
        ToolDescriptions.FileWrite,
        ToolSchema.Object()
            .String("path", ToolPathDescriptions.WorkspaceRelativePath, required: true, minLength: 1)
            .String(
                "content",
                "Full new file content as a JSON string (non-empty). Do not omit, null, or send a non-string type.",
                required: true,
                minLength: 1)
            .Build(),
        RequiresApproval: true);

    public async Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken cancellationToken = default)
    {
        if (!RoomDockerToolFactory.TryResolveContainerPath(
                invocation, guard, out var hostPath, out var containerPath, out var error))
        {
            return error;
        }

        if (!FileWriteSupport.TryGetContent(invocation, out var content, out error))
        {
            return error;
        }

        var roomId = RoomDockerToolFactory.RequireRoomId();
        if (await sandbox.IsDirectoryAsync(roomId, containerPath, cancellationToken).ConfigureAwait(false))
        {
            return ToolResult.Failure("Path is a directory", RoomDockerToolFactory.HostDisplay(hostPath));
        }

        var bytes = Encoding.UTF8.GetBytes(content);
        await sandbox.WriteFileAsync(roomId, containerPath, bytes, cancellationToken).ConfigureAwait(false);
        return ToolResult.Success(
            $"Wrote {content.Length} chars to {Path.GetFileName(hostPath)}",
            RoomDockerToolFactory.HostDisplay(hostPath));
    }
}

internal sealed class RoomDockerFileEditTool(IRoomDockerSandbox sandbox, WorkspaceGuard guard)
    : IAgentTool, ILocalWorkspaceTool
{
    public ToolDefinition Definition { get; } = new(
        "file_edit",
        ToolDescriptions.FileEdit(FileEditTool.MaxFileEditBytes),
        ToolSchema.Object()
            .String("path", ToolPathDescriptions.WorkspaceRelativePath, required: true, minLength: 1)
            .String("old_text", "Exact substring from the file (no line-number prefixes)", required: true, minLength: 1)
            .String("new_text", "Replacement (empty string deletes matched text)", required: true)
            .Boolean("replace_all", "Replace all occurrences", defaultValue: false)
            .Build(),
        RequiresApproval: true);

    public async Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken cancellationToken = default)
    {
        if (!RoomDockerToolFactory.TryResolveContainerPath(
                invocation, guard, out var hostPath, out var containerPath, out var error))
        {
            return error;
        }

        if (!ToolArguments.TryGetRequired(invocation, "old_text", out var oldText, out error))
        {
            return error;
        }

        if (!invocation.Arguments.TryGetString("new_text", out var newText))
        {
            newText = string.Empty;
        }

        var roomId = RoomDockerToolFactory.RequireRoomId();
        var bytes = await sandbox.ReadFileAsync(roomId, containerPath, cancellationToken).ConfigureAwait(false);
        if (bytes is null)
        {
            return ToolResult.Failure("File not found", RoomDockerToolFactory.HostDisplay(hostPath));
        }

        if (bytes.Length > FileEditTool.MaxFileEditBytes)
        {
            return ToolResult.Failure(
                "File too large for file_edit",
                $"Use apply_patch for large files ({bytes.Length:N0} bytes).");
        }

        var content = Encoding.UTF8.GetString(bytes);
        var replaceAll = invocation.Arguments.GetBoolean("replace_all");
        var match = FileEditMatcher.TryMatch(content, oldText, replaceAll);
        switch (match.Status)
        {
            case FileEditMatchStatus.NotFound:
                return ToolResult.Failure("Text not found", FileEditMatcher.BuildNotFoundMessage(content, oldText));
            case FileEditMatchStatus.NotUnique:
                return ToolResult.Failure(
                    "Text is not unique",
                    FileEditMatcher.BuildNotUniqueMessage(content, match.MatchedOldText, match.Occurrences));
        }

        var updated = FileEditMatcher.ApplyMatchedEdit(content, match, newText ?? string.Empty, replaceAll);
        await sandbox.WriteFileAsync(roomId, containerPath, Encoding.UTF8.GetBytes(updated), cancellationToken)
            .ConfigureAwait(false);
        return ToolResult.Success("Edited file", RoomDockerToolFactory.HostDisplay(hostPath));
    }
}
