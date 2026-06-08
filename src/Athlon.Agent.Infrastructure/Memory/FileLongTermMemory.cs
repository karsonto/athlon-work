using System.Text;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Memory;

namespace Athlon.Agent.Infrastructure.Memory;

public sealed class FileLongTermMemory(
    IAppPathProvider paths,
    IFileStorageService storage,
    AppSettings settings,
    IAppLogger logger) : ILongTermMemory
{
    private readonly IAppLogger _logger = logger.ForContext("FileLongTermMemory");
    private readonly MemorySettings _cfg = settings.Memory;

    private string MemoryDir => Path.Combine(paths.RootPath, _cfg.MemoryDirName);
    private string CuratedPath => Path.Combine(MemoryDir, _cfg.CuratedFileName);
    private string WatermarkPath => Path.Combine(MemoryDir, _cfg.WatermarkFileName);
    private string ArchiveDir => Path.Combine(MemoryDir, "archive");

    private string DailyPath(DateTime date) =>
        Path.Combine(MemoryDir, date.ToString("yyyy-MM-dd") + ".md");

    public Task<string> ReadCuratedAsync(CancellationToken ct = default)
    {
        var path = CuratedPath;
        if (!File.Exists(path)) return Task.FromResult(string.Empty);
        return File.ReadAllTextAsync(path, Encoding.UTF8, ct);
    }

    public async Task AppendDailyAsync(string text, CancellationToken ct = default)
    {
        Directory.CreateDirectory(MemoryDir);
        var path = DailyPath(DateTime.UtcNow);
        await File.AppendAllTextAsync(path, text, Encoding.UTF8, ct);
    }

    public Task<string> ReadDailyAsync(DateTime date, CancellationToken ct = default)
    {
        var path = DailyPath(date);
        if (!File.Exists(path)) return Task.FromResult(string.Empty);
        return File.ReadAllTextAsync(path, Encoding.UTF8, ct);
    }

    public Task<IReadOnlyList<string>> ListDailyFilesAfterAsync(DateTime watermark, CancellationToken ct = default)
    {
        if (!Directory.Exists(MemoryDir))
            return Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());

        var result = new List<string>();
        foreach (var filePath in Directory.GetFiles(MemoryDir, "????-??-??.md"))
        {
            var name = Path.GetFileName(filePath);
            if (name == _cfg.CuratedFileName || name == _cfg.WatermarkFileName)
                continue;
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
        var path = Path.Combine(MemoryDir, relativePath);
        if (!File.Exists(path)) return Task.FromResult(string.Empty);
        return File.ReadAllTextAsync(path, Encoding.UTF8, ct);
    }

    public async Task WriteCuratedAsync(string content, CancellationToken ct = default)
    {
        Directory.CreateDirectory(MemoryDir);
        await File.WriteAllTextAsync(CuratedPath, content, Encoding.UTF8, ct);
    }

    public Task<DateTime> ReadWatermarkAsync(CancellationToken ct = default)
    {
        if (!File.Exists(WatermarkPath))
            return Task.FromResult(DateTime.MinValue);
        var text = File.ReadAllText(WatermarkPath, Encoding.UTF8);
        if (DateTime.TryParse(text.Trim(), null, System.Globalization.DateTimeStyles.RoundtripKind, out var dt))
            return Task.FromResult(dt);
        return Task.FromResult(DateTime.MinValue);
    }

    public async Task WriteWatermarkAsync(DateTime watermark, CancellationToken ct = default)
    {
        Directory.CreateDirectory(MemoryDir);
        await File.WriteAllTextAsync(WatermarkPath, watermark.ToString("O"), Encoding.UTF8, ct);
    }

    public Task ArchiveDailyFileAsync(string relativePath, CancellationToken ct = default)
    {
        Directory.CreateDirectory(ArchiveDir);
        var src = Path.Combine(MemoryDir, relativePath);
        var dst = Path.Combine(ArchiveDir, relativePath);
        if (File.Exists(src))
            File.Move(src, dst, overwrite: true);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<string>> ListAllMemoryFilePathsAsync(CancellationToken ct = default)
    {
        var result = new List<string>();
        if (File.Exists(CuratedPath))
            result.Add(_cfg.MemoryDirName + "/" + _cfg.CuratedFileName);

        if (Directory.Exists(MemoryDir))
        {
            foreach (var filePath in Directory.GetFiles(MemoryDir, "*.md"))
            {
                var name = Path.GetFileName(filePath);
                if (name == _cfg.CuratedFileName || name == _cfg.WatermarkFileName)
                    continue;
                if (Path.GetExtension(name) != ".md")
                    continue;
                result.Add(_cfg.MemoryDirName + "/" + name);
            }
        }
        result.Sort(StringComparer.OrdinalIgnoreCase);
        return Task.FromResult<IReadOnlyList<string>>(result);
    }
}
