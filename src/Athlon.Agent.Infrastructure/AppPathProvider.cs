namespace Athlon.Agent.Infrastructure;

public interface IAppPathProvider
{
    string RootPath { get; }
    string ConfigPath { get; }
    string SessionsPath { get; }
    string AuditPath { get; }
    string LogsPath { get; }
    string CredentialsPath { get; }
    string SkillsPath { get; }

    string BehaviorPath => Path.Combine(RootPath, "behavior");

    void EnsureCreated();

    string ResolveSkillPath(string path);
}

public sealed class AppPathProvider : IAppPathProvider
{
    public const string AppDataFolderName = ".athlon-agent";
    public const string SkillsFolderName = "skills";

    public string RootPath { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), AppDataFolderName);
    public string ConfigPath => Path.Combine(RootPath, "config");
    public string SessionsPath => Path.Combine(RootPath, "sessions");
    public string AuditPath => Path.Combine(RootPath, "audit");
    public string LogsPath => Path.Combine(RootPath, "logs");
    public string CredentialsPath => Path.Combine(RootPath, "credentials");
    public string SkillsPath => Path.Combine(RootPath, SkillsFolderName);
    public string BehaviorPath => Path.Combine(RootPath, "behavior");

    public void EnsureCreated()
    {
        Directory.CreateDirectory(RootPath);
        Directory.CreateDirectory(ConfigPath);
        Directory.CreateDirectory(SessionsPath);
        Directory.CreateDirectory(AuditPath);
        Directory.CreateDirectory(LogsPath);
        Directory.CreateDirectory(CredentialsPath);
        Directory.CreateDirectory(SkillsPath);
        Directory.CreateDirectory(BehaviorPath);
    }

    public string ResolveSkillPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return path;
        }

        return Path.IsPathRooted(path) ? path : Path.Combine(SkillsPath, path);
    }
}
