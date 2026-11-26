using System.IO;
using Athlon.Agent.Core;

namespace Athlon.Agent.App.Services.Room;

/// <summary>Resolved room workspace binding against <see cref="AppSettings.Workspaces"/>.</summary>
public readonly record struct RoomWorkspaceBinding(
    string? Root,
    string? Id,
    WorkspaceKind Kind,
    WorkspaceSettings? Settings)
{
    public bool IsConfigured => !string.IsNullOrWhiteSpace(Root);

    public bool IsSsh => Kind == WorkspaceKind.Ssh;

    public static RoomWorkspaceBinding Empty => new(null, null, WorkspaceKind.Local, null);

    /// <summary>
    /// Resolve from room fields / request inputs. Does not fall back to the active Chat workspace.
    /// </summary>
    public static RoomWorkspaceBinding Resolve(
        AppSettings settings,
        string? workspaceId,
        string? workspaceRoot)
    {
        if (!string.IsNullOrWhiteSpace(workspaceId))
        {
            var match = settings.Workspaces.FirstOrDefault(w =>
                string.Equals(w.Id, workspaceId.Trim(), StringComparison.OrdinalIgnoreCase));
            if (match is not null && !string.IsNullOrWhiteSpace(match.RootPath))
            {
                return new RoomWorkspaceBinding(
                    match.RootPath.Trim(),
                    match.Id,
                    match.WorkspaceKind,
                    match);
            }
        }

        if (!string.IsNullOrWhiteSpace(workspaceRoot))
        {
            var trimmed = workspaceRoot.Trim();
            var byRoot = settings.Workspaces.FirstOrDefault(w =>
                !string.IsNullOrWhiteSpace(w.RootPath)
                && RootsEqual(w.RootPath.Trim(), trimmed, WorkspaceKinds.Parse(w.Kind)));
            if (byRoot is not null)
            {
                return new RoomWorkspaceBinding(
                    byRoot.RootPath.Trim(),
                    byRoot.Id,
                    byRoot.WorkspaceKind,
                    byRoot);
            }

            return new RoomWorkspaceBinding(trimmed, workspaceId?.Trim(), WorkspaceKind.Local, null);
        }

        return Empty;
    }

    public static RoomWorkspaceBinding Resolve(AppSettings settings, RoomInfo? room) =>
        room is null
            ? Empty
            : Resolve(settings, room.WorkspaceId, room.WorkspaceRoot);

    /// <summary>
    /// null = ok; otherwise an error code for the room settings / create API.
    /// </summary>
    public static string? Validate(
        AppSettings settings,
        string? workspaceId,
        string? workspaceRoot,
        string? agentRuntimeMode)
    {
        var binding = Resolve(settings, workspaceId, workspaceRoot);
        if (!binding.IsConfigured)
        {
            return "workspace_required";
        }

        var mode = RoomAgentRuntimeModes.Normalize(agentRuntimeMode);

        if (binding.IsSsh)
        {
            if (binding.Settings is null
                || binding.Settings.Ssh is null
                || string.IsNullOrWhiteSpace(binding.Id))
            {
                return "ssh_workspace_invalid";
            }

            if (string.IsNullOrWhiteSpace(binding.Settings.Ssh.Host)
                || string.IsNullOrWhiteSpace(binding.Settings.Ssh.Username))
            {
                return "ssh_workspace_invalid";
            }

            if (RoomAgentRuntimeModes.IsSandbox(mode))
            {
                return "ssh_workspace_sandbox_unsupported";
            }

            return null;
        }

        try
        {
            var full = Path.GetFullPath(binding.Root!);
            return Directory.Exists(full) ? null : "workspace_not_found";
        }
        catch
        {
            return "workspace_not_found";
        }
    }

    public static string FormatDisplayLabel(WorkspaceSettings workspace)
    {
        if (workspace.WorkspaceKind != WorkspaceKind.Ssh || workspace.Ssh is null)
        {
            return string.IsNullOrWhiteSpace(workspace.Name) ? workspace.RootPath : workspace.Name;
        }

        var ssh = workspace.Ssh;
        var host = ssh.Host?.Trim() ?? "";
        var name = workspace.Name?.Trim();
        if (!string.IsNullOrWhiteSpace(host) && !string.IsNullOrWhiteSpace(name))
        {
            return $"[SSH] {host}:{name}";
        }

        if (!string.IsNullOrWhiteSpace(name))
        {
            return $"[SSH] {name}";
        }

        return string.IsNullOrWhiteSpace(ssh.Username)
            ? $"[SSH] {(string.IsNullOrWhiteSpace(host) ? workspace.RootPath : host)}"
            : $"[SSH] {ssh.Username}@{ssh.Host}";
    }

    /// <summary>
    /// User chat uploads land under <c>{workspace}/user-upload</c> (local or SSH), not under
    /// a hidden room metadata tree.
    /// </summary>
    public static string AttachmentMirrorDir(string workspaceRoot, string roomId, WorkspaceKind kind)
    {
        _ = roomId;
        const string folder = "user-upload";
        if (kind == WorkspaceKind.Ssh)
        {
            return RemotePathNormalizer.Combine(
                RemotePathNormalizer.NormalizeRoot(workspaceRoot),
                folder);
        }

        return Path.Combine(Path.GetFullPath(workspaceRoot), folder);
    }

    private static bool RootsEqual(string configured, string candidate, WorkspaceKind kind)
    {
        if (kind == WorkspaceKind.Ssh)
        {
            return string.Equals(
                RemotePathNormalizer.NormalizeRoot(configured),
                RemotePathNormalizer.NormalizeRoot(candidate),
                StringComparison.Ordinal);
        }

        try
        {
            return string.Equals(
                Path.GetFullPath(configured),
                Path.GetFullPath(candidate),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }
        catch
        {
            return string.Equals(configured, candidate, StringComparison.OrdinalIgnoreCase);
        }
    }
}
