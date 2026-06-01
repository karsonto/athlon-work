namespace Athlon.Agent.Core;

/// <summary>Built-in directory names skipped by file search tools when no config override exists.</summary>
public static class WorkspaceIgnoreDefaults
{
    public static readonly string[] BuiltIn =
    [
        ".git", ".svn", ".hg",
        "bin", "obj", "target",
        "node_modules", "bower_components",
        "dist", "build", "out", ".next", ".nuxt", ".output", ".svelte-kit",
        "coverage", ".nyc_output",
        ".turbo", ".vite", ".parcel-cache",
        ".vs", "artifacts", "publish",
        "__pycache__", ".pytest_cache", "venv", ".venv"
    ];

    public static List<string> CreateMutableDefaultList() => [.. BuiltIn];
}
