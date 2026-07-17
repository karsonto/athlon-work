namespace Athlon.Agent.Core;

public static class WorkspaceSessionResolver
{
    public static WorkspaceSettings? FindMatch(AgentSession session, AppSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(session.ActiveWorkspaceId))
        {
            var byId = settings.Workspaces.FirstOrDefault(workspace =>
                string.Equals(workspace.Id, session.ActiveWorkspaceId, StringComparison.OrdinalIgnoreCase));
            if (byId is not null)
            {
                return byId;
            }
        }

        if (string.IsNullOrWhiteSpace(session.ActiveWorkspace))
        {
            return null;
        }

        return settings.Workspaces.FirstOrDefault(workspace =>
            !string.IsNullOrWhiteSpace(workspace.RootPath)
            && RootsEqual(workspace, session.ActiveWorkspace));
    }

    public static WorkspaceKind ResolveKind(AgentSession session, AppSettings settings)
    {
        var match = FindMatch(session, settings);
        if (match is not null)
        {
            return match.WorkspaceKind;
        }

        return string.IsNullOrWhiteSpace(session.ActiveWorkspaceId)
            ? WorkspaceKind.Local
            : WorkspaceKind.Ssh;
    }

    public static IReadOnlyList<string> ResolveIgnorePatterns(AgentSession session, AppSettings settings)
    {
        var match = FindMatch(session, settings);
        if (match is null && string.IsNullOrWhiteSpace(session.ActiveWorkspace))
        {
            var configured = settings.Workspaces.FirstOrDefault(workspace => !string.IsNullOrWhiteSpace(workspace.RootPath));
            return WorkspaceIgnoreResolver.Resolve(
                workspacePatterns: configured?.IgnorePatterns,
                globalPatterns: settings.WorkspaceIgnore.DirectoryNames);
        }

        return WorkspaceIgnoreResolver.Resolve(
            workspacePatterns: match?.IgnorePatterns,
            globalPatterns: settings.WorkspaceIgnore.DirectoryNames);
    }

    public static string? NormalizeRoot(AgentSession session, AppSettings settings)
    {
        if (string.IsNullOrWhiteSpace(session.ActiveWorkspace))
        {
            return null;
        }

        return ResolveKind(session, settings) == WorkspaceKind.Ssh
            ? RemotePathNormalizer.NormalizeRoot(session.ActiveWorkspace)
            : Path.GetFullPath(session.ActiveWorkspace);
    }

    private static bool RootsEqual(WorkspaceSettings workspace, string activeRoot)
    {
        if (workspace.WorkspaceKind == WorkspaceKind.Ssh)
        {
            return string.Equals(
                RemotePathNormalizer.NormalizeRoot(workspace.RootPath),
                RemotePathNormalizer.NormalizeRoot(activeRoot),
                StringComparison.Ordinal);
        }

        try
        {
            return string.Equals(
                Path.GetFullPath(workspace.RootPath),
                Path.GetFullPath(activeRoot),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}
