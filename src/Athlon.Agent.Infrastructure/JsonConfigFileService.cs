using System.Text.Json;

namespace Athlon.Agent.Infrastructure;

internal static class JsonConfigFileService
{
    public static async Task<T?> LoadAsync<T>(
        string path,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path))
        {
            return default;
        }

        var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        return Deserialize<T>(json);
    }

    public static Task SaveAsync<T>(
        string path,
        T value,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var json = JsonSerializer.Serialize(value, JsonFileStore.Options);
        return AtomicFile.WriteAllTextAsync(path, json, cancellationToken);
    }

    public static T? Deserialize<T>(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return default;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(json, JsonFileStore.Options);
        }
        catch
        {
            return default;
        }
    }
}
