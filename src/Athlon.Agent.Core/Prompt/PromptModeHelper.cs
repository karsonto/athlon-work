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



    public static bool IsDebugMode(EnvironmentPromptContext context) =>

        context.AgentMode == SessionAgentMode.Debug;



    public static bool HasTool(EnvironmentPromptContext context, string name) =>

        context.Tools.Any(tool => string.Equals(tool.Name, name, StringComparison.OrdinalIgnoreCase));



    public static bool HasAny(EnvironmentPromptContext context, params string[] names) =>

        names.Any(name => HasTool(context, name));



    public static bool HasKnowledgeTool(EnvironmentPromptContext context) =>

        HasTool(context, "knowledge_search");



    public static bool HasFileTools(EnvironmentPromptContext context) =>

        HasAny(context, "file_read", "file_write", "file_edit", "file_list");



    public static bool HasMcpGateway(EnvironmentPromptContext context) =>

        HasAny(context, "mcp_search", "mcp_call", "mcp_describe");

}

