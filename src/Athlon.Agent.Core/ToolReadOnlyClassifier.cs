namespace Athlon.Agent.Core;

public static class ToolReadOnlyClassifier
{
    private static readonly HashSet<string> ReadOnlyToolNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "file_read",
        "grep_files",
        "glob_files",
        "file_list",
        "knowledge_search",
        "load_skill_through_path",
        "memory_search"
    };

    public static bool IsReadOnly(string toolName) => ReadOnlyToolNames.Contains(toolName);
}
