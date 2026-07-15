using System.Text.Json;
using Athlon.Agent.Core.BehaviorReport;

namespace Athlon.Agent.Infrastructure.BehaviorReport;

public sealed class BehaviorEventLocalStore
{
    private readonly string _pendingPath;
    private readonly SemaphoreSlim _lock = new(1, 1);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public BehaviorEventLocalStore(IAppPathProvider paths)
    {
        Directory.CreateDirectory(paths.BehaviorPath);
        _pendingPath = Path.Combine(paths.BehaviorPath, "pending.jsonl");
    }

    public string PendingPath => _pendingPath;

    public async Task AppendAsync(BehaviorEvent evt, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_pendingPath)!);
            var line = JsonSerializer.Serialize(evt, JsonOptions) + Environment.NewLine;
            await File.AppendAllTextAsync(_pendingPath, line, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<IReadOnlyList<BehaviorEvent>> ReadAllAsync(CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_pendingPath))
            {
                return Array.Empty<BehaviorEvent>();
            }

            var lines = await File.ReadAllLinesAsync(_pendingPath, cancellationToken).ConfigureAwait(false);
            var events = new List<BehaviorEvent>();
            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                try
                {
                    var evt = JsonSerializer.Deserialize<BehaviorEvent>(line, JsonOptions);
                    if (evt is not null)
                    {
                        events.Add(evt);
                    }
                }
                catch
                {
                    // skip corrupt lines
                }
            }

            return events;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Rewrites the pending file keeping only events whose Ids are not in <paramref name="uploadedIds"/>.</summary>
    public async Task RemoveUploadedAsync(IReadOnlyCollection<string> uploadedIds, CancellationToken cancellationToken = default)
    {
        if (uploadedIds.Count == 0)
        {
            return;
        }

        var idSet = uploadedIds as HashSet<string> ?? new HashSet<string>(uploadedIds, StringComparer.Ordinal);
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_pendingPath))
            {
                return;
            }

            var lines = await File.ReadAllLinesAsync(_pendingPath, cancellationToken).ConfigureAwait(false);
            var remaining = new List<string>();
            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                try
                {
                    var evt = JsonSerializer.Deserialize<BehaviorEvent>(line, JsonOptions);
                    if (evt is null || !idSet.Contains(evt.Id))
                    {
                        remaining.Add(line);
                    }
                }
                catch
                {
                    remaining.Add(line);
                }
            }

            if (remaining.Count == 0)
            {
                File.Delete(_pendingPath);
            }
            else
            {
                await File.WriteAllLinesAsync(_pendingPath, remaining, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _lock.Release();
        }
    }
}
