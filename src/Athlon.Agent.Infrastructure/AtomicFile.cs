namespace Athlon.Agent.Infrastructure;

public static class AtomicFile
{
    public static Task WriteAllTextAsync(string path, string content, CancellationToken cancellationToken = default) =>
        FileIoRetry.RunAsync(
            () => WriteAllTextCoreAsync(path, content, cancellationToken),
            cancellationToken);

    private static async Task WriteAllTextCoreAsync(string path, string content, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // A fixed "<path>.tmp" name overwrites a real sibling the caller did not ask to touch.
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temp, content, cancellationToken).ConfigureAwait(false);
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
