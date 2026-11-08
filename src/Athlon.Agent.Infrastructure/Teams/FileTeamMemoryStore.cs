using System.Text.Json;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Teams;

namespace Athlon.Agent.Infrastructure.Teams;

public sealed class FileTeamMemoryStore(ITeamTaskStore taskStore) : ITeamMemoryStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task PutAsync(
        string rootSessionId,
        string taskId,
        TeamMemoryEntry entry,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (string.IsNullOrWhiteSpace(entry.Key))
        {
            throw new ArgumentException("Memory key is required.", nameof(entry));
        }

        entry.Key = entry.Key.Trim();
        entry.Content = Truncate(entry.Content, ITeamMemoryStore.MaxContentChars);
        entry.At = DateTimeOffset.UtcNow;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var entries = await ReadAllAsync(rootSessionId, taskId, cancellationToken).ConfigureAwait(false);
            entries.RemoveAll(e => string.Equals(e.Key, entry.Key, StringComparison.OrdinalIgnoreCase));
            entries.Add(entry);
            while (entries.Count > ITeamMemoryStore.MaxEntries)
            {
                entries.RemoveAt(0);
            }

            await WriteAllAsync(rootSessionId, taskId, entries, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<TeamMemoryEntry?> GetAsync(
        string rootSessionId,
        string taskId,
        string key,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return null;
        }

        var entries = await ReadAllAsync(rootSessionId, taskId, cancellationToken).ConfigureAwait(false);
        return entries.LastOrDefault(e => string.Equals(e.Key, key.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    public async Task<IReadOnlyList<TeamMemoryEntry>> ListAsync(
        string rootSessionId,
        string taskId,
        int limit = 40,
        CancellationToken cancellationToken = default)
    {
        var entries = await ReadAllAsync(rootSessionId, taskId, cancellationToken).ConfigureAwait(false);
        if (limit <= 0)
        {
            return entries;
        }

        return entries.Count <= limit ? entries : entries.TakeLast(limit).ToList();
    }

    public async Task<bool> DeleteAsync(
        string rootSessionId,
        string taskId,
        string key,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return false;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var entries = await ReadAllAsync(rootSessionId, taskId, cancellationToken).ConfigureAwait(false);
            var removed = entries.RemoveAll(e =>
                string.Equals(e.Key, key.Trim(), StringComparison.OrdinalIgnoreCase));
            if (removed == 0)
            {
                return false;
            }

            await WriteAllAsync(rootSessionId, taskId, entries, cancellationToken).ConfigureAwait(false);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    private string ResolvePath(string rootSessionId, string taskId) =>
        Path.Combine(taskStore.ResolveTaskDirectory(rootSessionId, taskId), "team-memory.jsonl");

    private async Task<List<TeamMemoryEntry>> ReadAllAsync(
        string rootSessionId,
        string taskId,
        CancellationToken cancellationToken)
    {
        var path = ResolvePath(rootSessionId, taskId);
        if (!File.Exists(path))
        {
            return [];
        }

        var list = new List<TeamMemoryEntry>();
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                var entry = JsonSerializer.Deserialize<TeamMemoryEntry>(line, JsonOptions);
                if (entry is not null && !string.IsNullOrWhiteSpace(entry.Key))
                {
                    list.Add(entry);
                }
            }
            catch (JsonException)
            {
                // Skip corrupt lines.
            }
        }

        return list;
    }

    private async Task WriteAllAsync(
        string rootSessionId,
        string taskId,
        IReadOnlyList<TeamMemoryEntry> entries,
        CancellationToken cancellationToken)
    {
        var path = ResolvePath(rootSessionId, taskId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        await using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        await using (var writer = new StreamWriter(stream))
        {
            foreach (var entry in entries)
            {
                var json = JsonSerializer.Serialize(entry, JsonOptions);
                await writer.WriteLineAsync(json.AsMemory(), cancellationToken).ConfigureAwait(false);
            }
        }

        File.Copy(tmp, path, overwrite: true);
        File.Delete(tmp);
    }

    private static string Truncate(string? text, int max)
    {
        text ??= "";
        text = text.Trim();
        return text.Length <= max ? text : text[..max] + "…";
    }
}
