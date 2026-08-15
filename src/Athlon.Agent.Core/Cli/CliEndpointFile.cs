using System.Diagnostics;
using System.Text.Json;

namespace Athlon.Agent.Core.Cli;

public static class CliEndpointFile
{
    public static CliEndpointInfo? TryRead(string rootPath)
    {
        var path = CliPaths.GetEndpointPath(rootPath);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<CliEndpointInfo>(json, JsonFileStoreOptions.Web);
        }
        catch
        {
            return null;
        }
    }

    public static void Write(string rootPath, CliEndpointInfo info)
    {
        var directory = CliPaths.GetDirectory(rootPath);
        Directory.CreateDirectory(directory);
        var json = JsonSerializer.Serialize(info, JsonFileStoreOptions.WebIndented);
        File.WriteAllText(CliPaths.GetEndpointPath(rootPath), json);
    }

    public static void Delete(string rootPath)
    {
        var path = CliPaths.GetEndpointPath(rootPath);
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best-effort cleanup on shutdown.
        }
    }

    public static bool IsProcessAlive(int pid)
    {
        if (pid <= 0)
        {
            return false;
        }

        try
        {
            var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    public static CliEndpointInfo? TryGetLive(string rootPath)
    {
        var info = TryRead(rootPath);
        if (info is null || !IsProcessAlive(info.Pid))
        {
            return null;
        }

        return info;
    }
}
