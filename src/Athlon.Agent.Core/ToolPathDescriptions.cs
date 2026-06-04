namespace Athlon.Agent.Core;

public static class ToolPathDescriptions
{
    public const string WorkspaceRelativePath =
        "Path relative to workspace root (forward slashes). Example: src/foo.cs. "
        + "Do NOT prefix with the workspace folder name or use an absolute path.";

    public const string OptionalWorkspaceRelativeDirectory =
        "Optional directory relative to workspace root (forward slashes). "
        + "Do NOT prefix with the workspace folder name.";

    public const string OptionalWorkspaceRelativeCwd =
        "Optional working directory relative to workspace root (forward slashes). "
        + "Defaults to workspace root. Quote paths with spaces or non-ASCII characters in cmd commands.";
}
