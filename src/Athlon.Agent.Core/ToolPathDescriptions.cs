namespace Athlon.Agent.Core;

public static class ToolPathDescriptions
{
    public const string WorkspaceRelativePath =
        "Workspace-relative path (forward slashes), e.g. src/foo.cs. Must be inside the active workspace.";

    public const string OptionalWorkspaceRelativeDirectory =
        "Optional directory path. Prefer workspace-relative (forward slashes); must be inside the active workspace.";

    /// <summary>Read-only explore tools (file_list / glob_files / grep_files) may search outside the workspace.</summary>
    public const string OptionalReadDirectory =
        "Optional directory or file path. Prefer workspace-relative (forward slashes); "
        + "absolute paths may be inside or outside the workspace.";

    public const string OptionalWorkspaceRelativeCwd =
        "Optional working directory. Prefer workspace-relative (forward slashes); absolute paths are allowed. "
        + "When workspace is configured, omitted cwd defaults to workspace root. "
        + "Quote paths with spaces or non-ASCII characters in cmd commands.";
}
