using Athlon.Agent.Core;
using Athlon.Agent.Infrastructure;

namespace Athlon.Agent.Tests;

public sealed class SkillConfigFileServiceTests
{
    [Fact]
    public async Task SaveAndLoad_RoundTripsSkillSettings()
    {
        var root = Path.Combine(Path.GetTempPath(), $"athlon-skills-json-{Guid.NewGuid():N}");
        var paths = new TestPathProvider(root);
        paths.EnsureCreated();

        var skills = new List<SkillSettings>
        {
            new() { Name = "demo_skill", Enabled = false, Path = "demo-skill" }
        };

        try
        {
            await SkillConfigFileService.SaveSkillsAsync(paths, skills);
            var loaded = await SkillConfigFileService.LoadSkillsAsync(paths);

            Assert.Single(loaded);
            Assert.Equal("demo_skill", loaded[0].Name);
            Assert.False(loaded[0].Enabled);
            Assert.Equal("demo-skill", loaded[0].Path);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    private sealed class TestPathProvider(string rootPath) : IAppPathProvider
    {
        public string RootPath { get; } = rootPath;
        public string ConfigPath => Path.Combine(rootPath, "config");
        public string SessionsPath => Path.Combine(rootPath, "sessions");
        public string AuditPath => Path.Combine(rootPath, "audit");
        public string LogsPath => Path.Combine(rootPath, "logs");
        public string CredentialsPath => Path.Combine(rootPath, "credentials");
        public string SkillsPath => Path.Combine(rootPath, "skills");
        public void EnsureCreated() => Directory.CreateDirectory(ConfigPath);
        public string ResolveSkillPath(string path) =>
            Path.IsPathRooted(path) ? path : Path.Combine(SkillsPath, path);
    }
}
