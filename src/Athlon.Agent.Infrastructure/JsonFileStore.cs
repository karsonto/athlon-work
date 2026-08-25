using System.IO;
using System.Text;
using System.Text.Json;
using Athlon.Agent.Core;

namespace Athlon.Agent.Infrastructure;

public interface IJsonFileStore
{
    Task SaveAsync<T>(string path, T value, CancellationToken cancellationToken = default);
    Task<T?> LoadAsync<T>(string path, CancellationToken cancellationToken = default);
    Task AppendJsonLineAsync(string path, object value, CancellationToken cancellationToken = default, bool prettyPrint = false);
}

public sealed class JsonFileStore : IJsonFileStore
{
    private static readonly UTF8Encoding Utf8Bom = new(encoderShouldEmitUTF8Identifier: true);
    private static readonly byte[] Utf8BomBytes = [0xEF, 0xBB, 0xBF];
    public static readonly JsonSerializerOptions Options = JsonFileStoreOptions.WebIndented;

    /// <summary>Single-line JSON for machine-friendly append logs (conversation/tool/audit).</summary>
    public static readonly JsonSerializerOptions JsonLineOptions = JsonFileStoreOptions.WebCompactRelaxed;

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
        return await JsonSerializer.DeserializeAsync<T>(stream, Options, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task AppendJsonLineAsync(string path, object value, CancellationToken cancellationToken = default, bool prettyPrint = false)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await EnsureUtf8BomForJsonlAsync(path, cancellationToken).ConfigureAwait(false);
        var options = prettyPrint ? Options : JsonLineOptions;
        var line = JsonSerializer.Serialize(value, options) + Environment.NewLine;
        await FileIoRetry.RunAsync(
            async () => await File.AppendAllTextAsync(path, line, Utf8Bom, cancellationToken).ConfigureAwait(false),
            cancellationToken);
    }

    private static async Task EnsureUtf8BomForJsonlAsync(string path, CancellationToken cancellationToken)
    {
        if (!path.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
        {
            return;
        }

        await FileIoRetry.RunAsync(async () =>
        {
            var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            if (bytes.Length == 0
                || (bytes.Length >= 3 && bytes[0] == Utf8BomBytes[0] && bytes[1] == Utf8BomBytes[1] && bytes[2] == Utf8BomBytes[2]))
            {
                return;
            }

            var migrated = new byte[Utf8BomBytes.Length + bytes.Length];
            Utf8BomBytes.CopyTo(migrated, 0);
            bytes.CopyTo(migrated, Utf8BomBytes.Length);
            await File.WriteAllBytesAsync(path, migrated, cancellationToken).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }
}
