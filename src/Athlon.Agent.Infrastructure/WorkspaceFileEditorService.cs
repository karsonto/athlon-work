using System.Text;
using Athlon.Agent.Core;

namespace Athlon.Agent.Infrastructure;

public sealed record WorkspaceFileOpenResult(
    bool Succeeded,
    string? Content,
    string? ErrorMessage,
    string? FullPath = null);

public sealed record WorkspaceFileSaveResult(bool Succeeded, string? ErrorMessage);

public sealed class WorkspaceFileEditorService(WorkspaceGuard guard, AppSettings settings)
{
    public async Task<WorkspaceFileOpenResult> TryOpenAsync(string path, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return new WorkspaceFileOpenResult(false, null, "文件不存在。");
        }

        var fullPath = Path.GetFullPath(path);
        if (!guard.IsInsideWorkspace(fullPath))
        {
            return new WorkspaceFileOpenResult(false, null, "只能打开工作区内的文件。");
        }

        if (!TextFileDetector.IsTextFile(fullPath))
        {
            return new WorkspaceFileOpenResult(
                false,
                null,
                "无法在编辑器中打开此类型文件，请使用系统默认程序。");
        }

        long length;
        try
        {
            length = new FileInfo(fullPath).Length;
        }
        catch (Exception exception)
        {
            return new WorkspaceFileOpenResult(false, null, exception.Message);
        }

        var maxBytes = settings.FileRead.MaxFileBytes;
        if (length > maxBytes)
        {
            return new WorkspaceFileOpenResult(
                false,
                null,
                $"文件过大（{length:N0} 字节），编辑器上限为 {maxBytes:N0} 字节。");
        }

        try
        {
            if (await TextFileDetector.LooksBinaryOnDiskAsync(fullPath, cancellationToken).ConfigureAwait(false))
            {
                return new WorkspaceFileOpenResult(
                    false,
                    null,
                    "无法在编辑器中打开此类型文件，请使用系统默认程序。");
            }

            var content = await File.ReadAllTextAsync(fullPath, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
            return new WorkspaceFileOpenResult(true, content, null, fullPath);
        }
        catch (Exception exception)
        {
            return new WorkspaceFileOpenResult(false, null, exception.Message);
        }
    }

    public async Task<WorkspaceFileSaveResult> SaveAsync(string path, string content, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return new WorkspaceFileSaveResult(false, "未指定文件路径。");
        }

        var fullPath = Path.GetFullPath(path);
        if (!guard.IsInsideWorkspace(fullPath))
        {
            return new WorkspaceFileSaveResult(false, "只能保存工作区内的文件。");
        }

        if (!TextFileDetector.IsTextFile(fullPath))
        {
            return new WorkspaceFileSaveResult(false, "无法保存此类型文件。");
        }

        try
        {
            await AtomicFile.WriteAllTextAsync(fullPath, content, cancellationToken).ConfigureAwait(false);
            return new WorkspaceFileSaveResult(true, null);
        }
        catch (Exception exception)
        {
            return new WorkspaceFileSaveResult(false, exception.Message);
        }
    }
}
