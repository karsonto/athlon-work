namespace Athlon.Agent.Infrastructure;

public static class AtomicFile
{
    public static Task WriteAllTextAsync(string path, string content, CancellationToken cancellationToken = default) =>
        FileIoRetry.RunAsync(
            () => WriteAllTextCoreAsync(path, content, cancellationToken),
            cancellationToken);

    private static async Task WriteAllTextCoreAsync(string path, string content, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        await File.WriteAllTextAsync(temp, content, cancellationToken).ConfigureAwait(false);
        File.Move(temp, path, true);
    }
}
