using System.Text.Encodings.Web;
using System.Text.Json;

namespace Athlon.Agent.Infrastructure;

public interface IJsonFileStore
{
    Task SaveAsync<T>(string path, T value, CancellationToken cancellationToken = default);
    Task<T?> LoadAsync<T>(string path, CancellationToken cancellationToken = default);
    Task AppendJsonLineAsync(string path, object value, CancellationToken cancellationToken = default, bool prettyPrint = false);
}

public sealed class JsonFileStore : IJsonFileStore
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        // Persist UTF-8 Chinese (and other non-ASCII) literally in session.json instead of \uXXXX.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Single-line JSON for machine-friendly append logs (conversation/tool/audit).</summary>
    public static readonly JsonSerializerOptions JsonLineOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public Task SaveAsync<T>(string path, T value, CancellationToken cancellationToken = default)
    {
        var json = JsonSerializer.Serialize(value, Options);
        return FileIoRetry.RunAsync(
            () => AtomicFile.WriteAllTextAsync(path, json, cancellationToken),
            cancellationToken);
    }

    public async Task<T?> LoadAsync<T>(string path, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path))
        {
            return default;
        }

        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<T>(stream, Options, cancellationToken);
    }

    public async Task AppendJsonLineAsync(string path, object value, CancellationToken cancellationToken = default, bool prettyPrint = false)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var options = prettyPrint ? Options : JsonLineOptions;
        var line = JsonSerializer.Serialize(value, options) + Environment.NewLine;
        await FileIoRetry.RunAsync(
            async () => await File.AppendAllTextAsync(path, line, cancellationToken).ConfigureAwait(false),
            cancellationToken);
    }
}
