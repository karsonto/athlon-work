namespace Athlon.Agent.Core;

public static class ToolPathDescriptions
{
    public const string WorkspaceRelativePath =
        "Prefer workspace-relative path (forward slashes), e.g. src/foo.cs. "
        + "Absolute paths are also allowed when needed.";

    public const string OptionalWorkspaceRelativeDirectory =
        "Optional directory path. Prefer workspace-relative (forward slashes); absolute paths are allowed.";

    public const string OptionalWorkspaceRelativeCwd =
        "Optional working directory. Prefer workspace-relative (forward slashes); absolute paths are allowed. "
        + "When workspace is configured, omitted cwd defaults to workspace root. "
        + "Quote paths with spaces or non-ASCII characters in cmd commands.";
}
