using Athlon.Agent.Core.Harness;

namespace Athlon.Agent.Core.Prompt;

public static class PromptModeHelper
{
    public static bool IsChatOnly(EnvironmentPromptContext context) =>
        !context.HasWorkspace;

    public static bool IsAgentMode(EnvironmentPromptContext context) =>
        context.AgentMode == SessionAgentMode.Agent;

    public static bool IsCodingMode(EnvironmentPromptContext context) =>
        context.AgentMode == SessionAgentMode.Coding;

    public static bool IsAskMode(EnvironmentPromptContext context) =>
        context.AgentMode == SessionAgentMode.Ask;

    public static bool IsPlanMode(EnvironmentPromptContext context) =>
        context.AgentMode == SessionAgentMode.Plan;

    public static bool HasKnowledgeTool(EnvironmentPromptContext context) =>
        context.Tools.Any(tool => string.Equals(tool.Name, "knowledge_search", StringComparison.OrdinalIgnoreCase));

    public static bool HasFileTools(EnvironmentPromptContext context) =>
        context.Tools.Any(tool => tool.Name is "file_read" or "file_write" or "file_edit" or "file_list");
}
