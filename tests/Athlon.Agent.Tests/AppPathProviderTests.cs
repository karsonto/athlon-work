using Athlon.Agent.Infrastructure;

namespace Athlon.Agent.Tests;

public sealed class AppPathProviderTests
{
    [Fact]
    public void SkillsPath_ResolvesUnderAthlonAgentRoot()
    {
        var paths = new AppPathProvider();
        var expected = Path.Combine(paths.RootPath, AppPathProvider.SkillsFolderName);

        Assert.Equal(expected, paths.SkillsPath);
    }

    [Fact]
    public void ResolveSkillPath_UsesSkillsDirectoryForRelativePaths()
    {
        var paths = new AppPathProvider();

        Assert.Equal(
            Path.Combine(paths.SkillsPath, "data-cleaning.yaml"),
            paths.ResolveSkillPath("data-cleaning.yaml"));
    }

    [Fact]
    public void EnsureCreated_CreatesSkillsDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "athlon-agent-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new TestAppPathProvider(root);
            paths.EnsureCreated();

            Assert.True(Directory.Exists(paths.SkillsPath));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    private sealed class TestAppPathProvider(string root) : IAppPathProvider
    {
        public string RootPath { get; } = root;
        public string ConfigPath => Path.Combine(RootPath, "config");
        public string SessionsPath => Path.Combine(RootPath, "sessions");
        public string AuditPath => Path.Combine(RootPath, "audit");
        public string LogsPath => Path.Combine(RootPath, "logs");
        public string CredentialsPath => Path.Combine(RootPath, "credentials");
        public string SkillsPath => Path.Combine(RootPath, AppPathProvider.SkillsFolderName);

        public void EnsureCreated()
        {
            Directory.CreateDirectory(RootPath);
            Directory.CreateDirectory(ConfigPath);
            Directory.CreateDirectory(SessionsPath);
            Directory.CreateDirectory(AuditPath);
            Directory.CreateDirectory(LogsPath);
            Directory.CreateDirectory(CredentialsPath);
            Directory.CreateDirectory(SkillsPath);
        }

        public string ResolveSkillPath(string path) =>
            string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path)
                ? path
                : Path.Combine(SkillsPath, path);
    }
}
