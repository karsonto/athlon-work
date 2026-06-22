namespace Athlon.Agent.Core.Prompt;

public static class PromptModeHelper
{
    public static bool IsChatOnly(EnvironmentPromptContext context) =>
        !context.HasWorkspace;

    public static bool HasKnowledgeTool(EnvironmentPromptContext context) =>
        context.Tools.Any(tool => string.Equals(tool.Name, "knowledge_search", StringComparison.OrdinalIgnoreCase));

    public static bool HasFileTools(EnvironmentPromptContext context) =>
        context.Tools.Any(tool => tool.Name is "file_read" or "file_write" or "file_edit" or "file_list");
}
