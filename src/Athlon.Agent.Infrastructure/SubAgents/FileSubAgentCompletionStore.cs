using Athlon.Agent.Core;
using Athlon.Agent.Core.SubAgents;

namespace Athlon.Agent.Infrastructure.SubAgents;

public sealed class FileSubAgentCompletionStore(IAppPathProvider paths, IJsonFileStore jsonFileStore) : ISubAgentCompletionStore
{
    public Task AppendAsync(string parentSessionId, PendingCompletion completion, CancellationToken cancellationToken = default) =>
        jsonFileStore.AppendJsonLineAsync(GetPath(parentSessionId), completion, cancellationToken);

    public async Task<IReadOnlyList<PendingCompletion>> DrainAsync(
        string parentSessionId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var path = GetPath(parentSessionId);
        if (!File.Exists(path))
        {
            return Array.Empty<PendingCompletion>();
        }

        var lines = await File.ReadAllLinesAsync(path, cancellationToken).ConfigureAwait(false);
        if (lines.Length == 0)
        {
            return Array.Empty<PendingCompletion>();
        }

        var take = Math.Max(1, limit);
        var drained = new List<PendingCompletion>();
        var remaining = new List<string>();

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            if (drained.Count < take)
            {
                var item = System.Text.Json.JsonSerializer.Deserialize<PendingCompletion>(line, JsonFileStore.JsonLineOptions);
                if (item is not null)
                {
                    drained.Add(item);
                }
            }
            else
            {
                remaining.Add(line);
            }
        }

        if (remaining.Count == 0)
        {
            File.Delete(path);
        }
        else
        {
            await File.WriteAllLinesAsync(path, remaining, cancellationToken).ConfigureAwait(false);
        }

        return drained;
    }

    public async Task<int> PeekCountAsync(string parentSessionId, CancellationToken cancellationToken = default)
    {
        var path = GetPath(parentSessionId);
        if (!File.Exists(path))
        {
            return 0;
        }

        var lines = await File.ReadAllLinesAsync(path, cancellationToken).ConfigureAwait(false);
        return lines.Count(line => !string.IsNullOrWhiteSpace(line));
    }

    private string GetPath(string parentSessionId) =>
        Path.Combine(paths.SessionsPath, parentSessionId, "subagent_completions.jsonl");
}
