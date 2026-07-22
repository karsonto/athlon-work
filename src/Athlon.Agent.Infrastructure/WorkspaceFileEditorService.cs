using System.Text;
using Athlon.Agent.Core;

namespace Athlon.Agent.Infrastructure;

public sealed record WorkspaceFileOpenResult(
    bool Succeeded,
    string? Content,
    string? ErrorMessage,
    string? FullPath = null);

public sealed record WorkspaceFileSaveResult(bool Succeeded, string? ErrorMessage);

public sealed class WorkspaceFileEditorService(
    WorkspaceGuard guard,
    AppSettings settings,
    ISshWorkspaceClient sshClient)
{
    public Task<WorkspaceFileOpenResult> TryOpenAsync(string path, CancellationToken cancellationToken = default) =>
        guard.CurrentKind == WorkspaceKind.Ssh
            ? TryOpenRemoteAsync(path, cancellationToken)
            : TryOpenLocalAsync(path, cancellationToken);

    public Task<WorkspaceFileSaveResult> SaveAsync(string path, string content, CancellationToken cancellationToken = default) =>
        guard.CurrentKind == WorkspaceKind.Ssh
            ? SaveRemoteAsync(path, content, cancellationToken)
            : SaveLocalAsync(path, content, cancellationToken);

    private async Task<WorkspaceFileOpenResult> TryOpenLocalAsync(string path, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return new WorkspaceFileOpenResult(false, null, "文件不存在。");
        }

        if (Directory.Exists(path))
        {
            return new WorkspaceFileOpenResult(false, null, "无法打开目录。");
        }

        var fullPath = Path.GetFullPath(path);
        if (!guard.IsInsideWorkspace(fullPath))
        {
            return new WorkspaceFileOpenResult(false, null, "只能打开工作区内的文件。");
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
            var content = await File.ReadAllTextAsync(fullPath, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
            return new WorkspaceFileOpenResult(true, content, null, fullPath);
        }
        catch (Exception exception)
        {
            return new WorkspaceFileOpenResult(false, null, exception.Message);
        }
    }

    private async Task<WorkspaceFileOpenResult> TryOpenRemoteAsync(string path, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return new WorkspaceFileOpenResult(false, null, "文件不存在。");
        }

        if (!sshClient.IsConnected)
        {
            return new WorkspaceFileOpenResult(false, null, "SSH 未连接。");
        }

        var fullPath = guard.Normalize(path);
        if (!guard.IsInsideWorkspace(fullPath))
        {
            return new WorkspaceFileOpenResult(false, null, "只能打开工作区内的文件。");
        }

        SshFileInfo? info;
        try
        {
            info = await sshClient.TryGetFileInfoAsync(fullPath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            return new WorkspaceFileOpenResult(false, null, exception.Message);
        }

        if (info is null || info.IsDirectory)
        {
            return new WorkspaceFileOpenResult(false, null, "文件不存在。");
        }

        var maxBytes = settings.FileRead.MaxFileBytes;
        if (info.Length > maxBytes)
        {
            return new WorkspaceFileOpenResult(
                false,
                null,
                $"文件过大（{info.Length:N0} 字节），编辑器上限为 {maxBytes:N0} 字节。");
        }

        try
        {
            var content = await sshClient.ReadTextAsync(fullPath, cancellationToken).ConfigureAwait(false);
            return new WorkspaceFileOpenResult(true, content, null, fullPath);
        }
        catch (Exception exception)
        {
            return new WorkspaceFileOpenResult(false, null, exception.Message);
        }
    }

    private async Task<WorkspaceFileSaveResult> SaveLocalAsync(string path, string content, CancellationToken cancellationToken)
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

    private async Task<WorkspaceFileSaveResult> SaveRemoteAsync(string path, string content, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return new WorkspaceFileSaveResult(false, "未指定文件路径。");
        }

        if (!sshClient.IsConnected)
        {
            return new WorkspaceFileSaveResult(false, "SSH 未连接。");
        }

        var fullPath = guard.Normalize(path);
        if (!guard.IsInsideWorkspace(fullPath))
        {
            return new WorkspaceFileSaveResult(false, "只能保存工作区内的文件。");
        }

        try
        {
            await sshClient.WriteTextAsync(fullPath, content, cancellationToken).ConfigureAwait(false);
            return new WorkspaceFileSaveResult(true, null);
        }
        catch (Exception exception)
        {
            return new WorkspaceFileSaveResult(false, exception.Message);
        }
    }
}
