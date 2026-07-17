using System.Text;

namespace Athlon.Agent.Core.Prompt;

public sealed class WorkspacePolicySection : IEnvironmentPromptSection
{
    public int Order => 300;

    public void Append(StringBuilder builder, EnvironmentPromptContext context)
    {
        if (!context.HasWorkspace)
        {
            builder.AppendLine("当前未配置工作区，处于纯对话模式。");
            builder.AppendLine("你无法访问本地文件、执行命令或使用 MCP 工具。");
            builder.AppendLine("如需进行代码编辑、文件操作或命令执行，请引导用户在应用中配置工作区。");
            if (PromptModeHelper.HasKnowledgeTool(context))
            {
                builder.AppendLine("当前会话已启用知识库检索（knowledge_search），可用于回答与已上传文档相关的问题。");
                builder.AppendLine("文件与命令类工具仍不可用。");
            }

            builder.AppendLine();
            return;
        }

        builder.AppendLine("Active workspace information:");
        if (context.WorkspaceKind == WorkspaceKind.Ssh)
        {
            builder.AppendLine("This is an SSH remote workspace. Paths are remote Unix paths (forward slashes).");
        }

        builder.AppendLine("In file tool arguments (path), prefer forward slashes (/), e.g. src/foo.cs, even on Windows.");
        builder.AppendLine("Relative paths resolve from the active workspace root below; all paths must stay inside the workspace root.");
        builder.AppendLine($"When using relative paths, use src/foo.cs. Avoid prefixing with {context.WorkspaceName}/.");
        builder.AppendLine($"Active workspace label: {context.WorkspaceName} (informational, not a path prefix).");
        builder.AppendLine($"Workspace root: {context.WorkspaceRoot}");
        builder.AppendLine("Workspace contents are intentionally not embedded in this prompt because they change often.");
        builder.AppendLine("Use file_list to fetch a live directory listing when needed.");
        builder.AppendLine();
    }
}
