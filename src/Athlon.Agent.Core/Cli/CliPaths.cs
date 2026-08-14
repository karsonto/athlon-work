namespace Athlon.Agent.Core.Cli;

public static class CliPaths
{
    public const string FolderName = "cli";
    public const string EndpointFileName = "endpoint.json";
    public const string SessionsFileName = "sessions.json";
    public const string AppDataFolderName = ".athlon-agent";

    public static string DefaultRootPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), AppDataFolderName);

    public static string GetDirectory(string rootPath) => Path.Combine(rootPath, FolderName);

    public static string GetEndpointPath(string rootPath) =>
        Path.Combine(GetDirectory(rootPath), EndpointFileName);

    public static string GetSessionsMapPath(string rootPath) =>
        Path.Combine(GetDirectory(rootPath), SessionsFileName);

    public static string NormalizeLocalPath(string path)
    {
        var full = Path.GetFullPath(path);
        if (full.Length == 3 && full[1] == ':')
        {
            return full;
        }

        return full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }
}
