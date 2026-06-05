using System.Text;

namespace Athlon.Agent.Core.Prompt;

public sealed class WorkspacePolicySection : IEnvironmentPromptSection
{
    public int Order => 300;

    public void Append(StringBuilder builder, EnvironmentPromptContext context)
    {
        if (!context.HasWorkspace)
        {
            builder.AppendLine("当前工作区尚未设定。");
            builder.AppendLine("文件工具仍可使用：绝对路径将按系统路径解析；相对路径将基于当前进程目录。");
            builder.AppendLine("如需稳定结果，请先让用户设置 Workspace，或优先使用绝对路径。");
            builder.AppendLine();
            return;
        }

        builder.AppendLine("Active workspace information:");
        builder.AppendLine("In file tool arguments (path), prefer forward slashes (/), e.g. src/foo.cs, even on Windows.");
        builder.AppendLine("Relative paths resolve from the active workspace root below; absolute paths are also allowed.");
        builder.AppendLine($"When using relative paths, use src/foo.cs. Avoid prefixing with {context.WorkspaceName}/.");
        builder.AppendLine($"Active workspace label: {context.WorkspaceName} (informational, not a path prefix).");
        builder.AppendLine($"Workspace root: {context.WorkspaceRoot}");
        builder.AppendLine("Workspace contents are intentionally not embedded in this prompt because they change often.");
        builder.AppendLine("Use file_list to fetch a live directory listing when needed; use absolute paths for non-workspace files.");
        builder.AppendLine();
    }
}
