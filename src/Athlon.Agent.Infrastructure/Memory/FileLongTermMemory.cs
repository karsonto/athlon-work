using System.Text;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Memory;

namespace Athlon.Agent.Infrastructure.Memory;

public sealed class FileLongTermMemory(
    IAppPathProvider paths,
    IActiveWorkspaceContext workspaceContext,
    IActiveAgentSessionContext sessionContext,
    AppSettings settings,
    IAppLogger logger) : ILongTermMemory
{
    private readonly IAppLogger _logger = logger.ForContext("FileLongTermMemory");
    private readonly MemorySettings _cfg = settings.Memory;

    public bool HasActiveScope =>
        MemoryScopeResolver.TryResolve(workspaceContext, sessionContext, out _, out _);

    public string? ActiveWorkspaceKey =>
        MemoryScopeResolver.TryResolveWorkspaceKey(workspaceContext, out var key) ? key : null;

    public string? ActiveSessionId
    {
        get
        {
            var id = sessionContext.SessionId?.Trim();
            return string.IsNullOrWhiteSpace(id) ? null : id;
        }
    }

    private bool TryGetMemoryDir(out string memoryDir)
    {
        memoryDir = string.Empty;
        if (!MemoryScopeResolver.TryResolve(workspaceContext, sessionContext, out var workspaceKey, out var sessionId))
        {
            return false;
        }

        memoryDir = MemoryScopeResolver.BuildMemoryDir(paths.RootPath, _cfg.MemoryDirName, workspaceKey, sessionId);
        return true;
    }

    private string? TryGetMemoryDirOrNull() =>
        TryGetMemoryDir(out var dir) ? dir : null;

    public Task<string> ReadCuratedAsync(CancellationToken ct = default)
    {
        if (!TryGetMemoryDir(out var memoryDir))
        {
            return Task.FromResult(string.Empty);
        }

        var path = Path.Combine(memoryDir, _cfg.CuratedFileName);
        if (!File.Exists(path))
        {
            return Task.FromResult(string.Empty);
        }

        return File.ReadAllTextAsync(path, Encoding.UTF8, ct);
    }

    public async Task AppendDailyAsync(string text, CancellationToken ct = default)
    {
        if (!TryGetMemoryDir(out var memoryDir))
        {
            return;
        }

        Directory.CreateDirectory(memoryDir);
        var path = Path.Combine(memoryDir, DateTime.UtcNow.ToString("yyyy-MM-dd") + ".md");
        await File.AppendAllTextAsync(path, text, Encoding.UTF8, ct).ConfigureAwait(false);
    }

    public Task<string> ReadDailyAsync(DateTime date, CancellationToken ct = default)
    {
        if (!TryGetMemoryDir(out var memoryDir))
        {
            return Task.FromResult(string.Empty);
        }

        var path = Path.Combine(memoryDir, date.ToString("yyyy-MM-dd") + ".md");
        if (!File.Exists(path))
        {
            return Task.FromResult(string.Empty);
        }

        return File.ReadAllTextAsync(path, Encoding.UTF8, ct);
    }

    public Task<IReadOnlyList<string>> ListDailyFilesAfterAsync(DateTime watermark, CancellationToken ct = default)
    {
        if (!TryGetMemoryDir(out var memoryDir) || !Directory.Exists(memoryDir))
        {
            return Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
        }

        var result = new List<string>();
        foreach (var filePath in Directory.GetFiles(memoryDir, "????-??-??.md"))
        {
            var name = Path.GetFileName(filePath);
            if (name == _cfg.CuratedFileName || name == _cfg.WatermarkFileName)
            {
                continue;
            }

            var lastWrite = File.GetLastWriteTimeUtc(filePath);
            if (lastWrite > watermark)
            {
                result.Add(name);
            }
        }

        result.Sort(StringComparer.OrdinalIgnoreCase);
        return Task.FromResult<IReadOnlyList<string>>(result);
    }

    public Task<string> ReadDailyFileAsync(string relativePath, CancellationToken ct = default)
    {
        if (!TryGetMemoryDir(out var memoryDir))
        {
            return Task.FromResult(string.Empty);
        }

        var safeName = Path.GetFileName(relativePath);
        if (string.IsNullOrWhiteSpace(safeName))
        {
            return Task.FromResult(string.Empty);
        }

        var path = Path.Combine(memoryDir, safeName);
        if (!File.Exists(path))
        {
            return Task.FromResult(string.Empty);
        }

        return File.ReadAllTextAsync(path, Encoding.UTF8, ct);
    }

    public async Task WriteCuratedAsync(string content, CancellationToken ct = default)
    {
        if (!TryGetMemoryDir(out var memoryDir))
        {
            return;
        }

        Directory.CreateDirectory(memoryDir);
        var path = Path.Combine(memoryDir, _cfg.CuratedFileName);
        await File.WriteAllTextAsync(path, content, Encoding.UTF8, ct).ConfigureAwait(false);
    }

    public Task<DateTime> ReadWatermarkAsync(CancellationToken ct = default)
    {
        if (!TryGetMemoryDir(out var memoryDir))
        {
            return Task.FromResult(DateTime.MinValue);
        }

        var path = Path.Combine(memoryDir, _cfg.WatermarkFileName);
        if (!File.Exists(path))
        {
            return Task.FromResult(DateTime.MinValue);
        }

        var text = File.ReadAllText(path, Encoding.UTF8);
        if (DateTime.TryParse(text.Trim(), null, System.Globalization.DateTimeStyles.RoundtripKind, out var dt))
        {
            return Task.FromResult(dt);
        }

        return Task.FromResult(DateTime.MinValue);
    }

    public async Task WriteWatermarkAsync(DateTime watermark, CancellationToken ct = default)
    {
        if (!TryGetMemoryDir(out var memoryDir))
        {
            return;
        }

        Directory.CreateDirectory(memoryDir);
        var path = Path.Combine(memoryDir, _cfg.WatermarkFileName);
        await File.WriteAllTextAsync(path, watermark.ToString("O"), Encoding.UTF8, ct).ConfigureAwait(false);
    }

    public Task ArchiveDailyFileAsync(string relativePath, CancellationToken ct = default)
    {
        if (!TryGetMemoryDir(out var memoryDir))
        {
            return Task.CompletedTask;
        }

        var safeName = Path.GetFileName(relativePath);
        if (string.IsNullOrWhiteSpace(safeName))
        {
            return Task.CompletedTask;
        }

        var archiveDir = Path.Combine(memoryDir, "archive");
        Directory.CreateDirectory(archiveDir);
        var src = Path.Combine(memoryDir, safeName);
        var dst = Path.Combine(archiveDir, safeName);
        if (File.Exists(src))
        {
            File.Move(src, dst, overwrite: true);
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<string>> ListAllMemoryFilePathsAsync(CancellationToken ct = default)
    {
        if (!TryGetMemoryDir(out var memoryDir))
        {
            return Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
        }

        var result = new List<string>();
        var curated = Path.Combine(memoryDir, _cfg.CuratedFileName);
        if (File.Exists(curated))
        {
            result.Add(_cfg.CuratedFileName);
        }

        if (Directory.Exists(memoryDir))
        {
            foreach (var filePath in Directory.GetFiles(memoryDir, "*.md"))
            {
                var name = Path.GetFileName(filePath);
                if (name == _cfg.CuratedFileName || name == _cfg.WatermarkFileName)
                {
                    continue;
                }

                result.Add(name);
            }
        }

        result.Sort(StringComparer.OrdinalIgnoreCase);
        return Task.FromResult<IReadOnlyList<string>>(result);
    }

    public Task DeleteCurrentSessionMemoryAsync(CancellationToken cancellationToken = default)
    {
        if (!MemoryScopeResolver.TryResolve(workspaceContext, sessionContext, out var workspaceKey, out var sessionId))
        {
            return Task.CompletedTask;
        }

        return DeleteSessionMemoryAsync(workspaceKey, sessionId, cancellationToken);
    }

    public Task DeleteSessionMemoryAsync(
        string? workspaceKey,
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return Task.CompletedTask;
        }

        var key = workspaceKey;
        if (string.IsNullOrWhiteSpace(key)
            && !MemoryScopeResolver.TryResolveWorkspaceKey(workspaceContext, out key))
        {
            return Task.CompletedTask;
        }

        var dir = MemoryScopeResolver.BuildMemoryDir(paths.RootPath, _cfg.MemoryDirName, key!, sessionId);
        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
                _logger.Information("Deleted session memory directory {Path}", dir);
            }
        }
        catch (Exception ex)
        {
            _logger.Warning("Failed to delete session memory at {Path}: {Message}", dir, ex.Message);
        }

        return Task.CompletedTask;
    }
}
