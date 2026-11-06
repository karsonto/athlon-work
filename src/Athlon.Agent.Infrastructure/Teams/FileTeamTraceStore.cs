using System.Text;
using System.Text.Json;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Teams;

namespace Athlon.Agent.Infrastructure.Teams;

public sealed class FileTeamTraceStore(ITeamTaskStore taskStore, IJsonFileStore jsonFileStore) : ITeamTraceStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    public async Task AppendAsync(
        string rootSessionId,
        string taskId,
        TeamTraceEvent evt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(evt);
        var dir = taskStore.ResolveTaskDirectory(rootSessionId, taskId);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "trace.jsonl");
        var line = JsonSerializer.Serialize(evt, JsonOptions) + Environment.NewLine;
        await File.AppendAllTextAsync(path, line, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<TeamTraceEvent>> ReadAsync(
        string rootSessionId,
        string taskId,
        CancellationToken cancellationToken = default)
    {
        var path = Path.Combine(taskStore.ResolveTaskDirectory(rootSessionId, taskId), "trace.jsonl");
        if (!File.Exists(path))
        {
            return [];
        }

        var lines = await File.ReadAllLinesAsync(path, cancellationToken).ConfigureAwait(false);
        var events = new List<TeamTraceEvent>();
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                var evt = JsonSerializer.Deserialize<TeamTraceEvent>(line, JsonOptions);
                if (evt is not null)
                {
                    events.Add(evt);
                }
            }
            catch (JsonException)
            {
                // skip corrupt lines
            }
        }

        return events;
    }

    public async Task SaveLayoutAsync(
        string rootSessionId,
        string taskId,
        IReadOnlyDictionary<string, TeamTraceNodeLayout> layout,
        CancellationToken cancellationToken = default)
    {
        // Layout-only write — never re-save task.json (avoids stomping live worker progress).
        var dir = taskStore.ResolveTaskDirectory(rootSessionId, taskId);
        if (!Directory.Exists(dir))
        {
            return;
        }

        var path = Path.Combine(dir, "layout.json");
        var dict = layout.ToDictionary(
            kv => kv.Key,
            kv => kv.Value,
            StringComparer.OrdinalIgnoreCase);
        await jsonFileStore.SaveAsync(path, dict, cancellationToken).ConfigureAwait(false);
    }
}
