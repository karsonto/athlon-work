namespace Athlon.Agent.Core;

/// <summary>
/// Shared ToolDefinition.Description strings for local and SSH twins.
/// Keep schema-level contracts here; workflow / decision trees live in prompt sections.
/// </summary>
public static class ToolDescriptions
{
    public const string FileRead =
        "Read file content exactly as stored on disk. Body has no line-number prefixes; "
        + "line numbers appear only in the footer (start_line, end_line, next_start_line). "
        + "Use 1-based start_line/end_line for chunks; when truncated:true or next_start_line is set, "
        + "continue from that line. file_edit old_text must copy this body, not a line-number prefix.";

    public static string FileEdit(long maxFileEditBytes) =>
        "Replace exact text in a file. old_text must match the file body exactly "
        + "(no line-number prefixes from file_read). "
        + "For multi-line or >= 5 lines of old_text, or files larger than "
        + $"{maxFileEditBytes / 1024} KiB, prefer apply_patch. "
        + "On mismatch: re-read once with corrected old_text; if still failing use apply_patch "
        + "(or file_write for small files).";

    public const string FileWrite =
        "Create or overwrite a file. `content` must be a non-empty JSON string with the full file body. "
        + "Put `path` before `content` in arguments so streaming truncation still preserves the path. "
        + "For large HTML/JS, write a short skeleton then extend with file_edit or apply_patch.";

    public static string GrepFiles(int maxFilesToScan, int maxMatches) =>
        "Search file contents. Literal matching by default (case-insensitive); set regex true for .NET regular expressions. "
        + $"Scans up to {maxFilesToScan} files, returns up to {maxMatches} matches.";

    public const string ApplyPatch =
        "Apply a patch to workspace files. Prefer this shape:\n"
        + "*** Begin Patch\n"
        + "*** Update File: path/to/file\n"
        + "@@ Class or function name\n"
        + " context copied from the file\n"
        + "-old line\n"
        + "+new line\n"
        + "*** End Patch\n"
        + "@@ is a context anchor, not a line number. Context and removed lines must be copied from the file. "
        + "Also accepts a unified diff (--- / +++ / @@). Optional path limits application to that file. "
        + "On failure the error includes the lines that were not found.";

    public static string GlobFiles(int maxMatches) =>
        "Find files by name using a glob pattern (recursive with **, respects workspace ignore rules). "
        + "Supports ** and {a,b} brace expansions. Directories are suffixed with /. "
        + $"Matching is case-insensitive. Returns up to {maxMatches} matches.";

    public static string ExecuteCommandLocal(int defaultBlockUntilMs, int defaultTimeoutSeconds, int maxTimeoutSeconds) =>
        "Execute a shell command (user approval required). On Windows use cmd.exe semantics, not PowerShell. "
        + "Console I/O prefers UTF-8 (chcp 65001) and normalizes captured output to UTF-8. "
        + $"block_until_ms (default {defaultBlockUntilMs}, 0 = return as soon as the process starts) only returns control. "
        + "A still-running process stays in the background until it exits, timeout, or command_kill. "
        + $"Default timeout {defaultTimeoutSeconds}s (max {maxTimeoutSeconds}s) stops the process; it does not end the agent turn.";

    public const string TodoWrite =
        "Create or update the session task list. "
        + "Pass todos as an array of objects with id, content, and status "
        + "(pending, in_progress, completed, cancelled). "
        + "Use stable kebab-case ids (e.g. verify-compile). Keep content as one verifiable step. "
        + "Prefer at most one in_progress item at a time. "
        + "Set merge=false to replace the full list (first write); merge=true to update existing items by id.";

    public const string AskUser =
        "Ask the user 1–3 multiple-choice questions (each with at least two concrete options). "
        + "Optional allow_free_text lets the user add notes. Schema fields: questions[].id, prompt, options, allow_multiple, allow_free_text.";

    public const string ComputerObserve =
        "Capture the desktop: screenshot, frame_id, foreground window, cursor, display geometry, and an optional bounded UI Automation tree. "
        + "Nodes carry physical bounds and image_bounds relative to the screenshot. "
        + "Defaults to the monitor under the cursor; pass monitor_index or window_title to target a display or app.";

    public const string ComputerInteract =
        "Perform exactly one desktop action against a fresh frame_id. "
        + "Prefer element_id (host acts through the control). "
        + "Use image_x/image_y for canvas or self-drawn targets; right_click and drag require them. "
        + "Returns a post-action screenshot and a new frame_id.";

    public const string BrowserScreenshot =
        "Capture the open Browser tab (visible area by default). "
        + "Set full_page true for the whole document (clipped at 16384 CSS pixels). "
        + "Result includes screenshot_file for citing the capture.";
}
