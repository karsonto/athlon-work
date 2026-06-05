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
            builder.AppendLine("请让用户通过侧栏「配置」或设置页的 Workspace 指定工作区目录后，再使用 file_list、file_read、file_write、file_edit、grep_files、glob_files 等文件工具。");
            builder.AppendLine("在工作区未设定前，不要假设任何文件路径，也不要调用访问工作区文件的工具。");
            builder.AppendLine();
            return;
        }

        builder.AppendLine("All relative file paths are resolved from the active workspace. Never access files outside the configured workspace.");
        builder.AppendLine("In file tool arguments (path), always use forward slashes (/), e.g. src/foo.cs — not backslashes (\\), even on Windows.");
        builder.AppendLine("Paths are relative to Workspace root below — not a parent directory, and not an absolute path.");
        builder.AppendLine($"Correct: src/foo.cs. Wrong: {context.WorkspaceName}/src/foo.cs or the full Workspace root path in path.");
        builder.AppendLine($"Active workspace label: {context.WorkspaceName} (not a path prefix — do not include in file tool path).");
        builder.AppendLine($"Workspace root: {context.WorkspaceRoot}");
        builder.AppendLine("Workspace contents are intentionally not embedded in this prompt because they change often.");
        builder.AppendLine("Use file_list to fetch a live directory listing when needed.");
        builder.AppendLine();
    }
}
