using System.Text.Json;

namespace Athlon.Agent.Infrastructure;

public interface ISessionTaskStore
{
    Task<IReadOnlyList<AgentTaskItem>> LoadAsync(string sessionId, CancellationToken cancellationToken = default);

    Task SaveAsync(string sessionId, IReadOnlyList<AgentTaskItem> tasks, CancellationToken cancellationToken = default);
}

public sealed class FileSessionTaskStore(IFileStorageService storage) : ISessionTaskStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public async Task<IReadOnlyList<AgentTaskItem>> LoadAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        var path = ResolvePath(sessionId);
        if (!File.Exists(path))
        {
            return Array.Empty<AgentTaskItem>();
        }

        await using var stream = File.OpenRead(path);
        var tasks = await JsonSerializer.DeserializeAsync<List<AgentTaskItem>>(stream, JsonOptions, cancellationToken);
        return tasks ?? [];
    }

    public async Task SaveAsync(string sessionId, IReadOnlyList<AgentTaskItem> tasks, CancellationToken cancellationToken = default)
    {
        var path = ResolvePath(sessionId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, tasks, JsonOptions, cancellationToken);
    }

    private static string ResolvePath(string sessionId) =>
        Path.Combine(storage.RootPath, "sessions", sessionId, "tasks.json");
}
