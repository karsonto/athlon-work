namespace Athlon.Agent.Core;

public enum WorkspaceKind
{
    Local = 0,
    Ssh = 1
}

public static class WorkspaceKinds
{
    public const string Local = "local";
    public const string Ssh = "ssh";

    public static WorkspaceKind Parse(string? value) =>
        string.Equals(value, Ssh, StringComparison.OrdinalIgnoreCase)
            ? WorkspaceKind.Ssh
            : WorkspaceKind.Local;

    public static string ToSettingsValue(WorkspaceKind kind) =>
        kind == WorkspaceKind.Ssh ? Ssh : Local;
}
