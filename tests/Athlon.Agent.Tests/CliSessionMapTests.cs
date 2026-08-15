using Athlon.Agent.Core.Cli;
using Athlon.Agent.Infrastructure;
using Athlon.Agent.Infrastructure.Cli;

namespace Athlon.Agent.Tests;

public sealed class CliSessionMapTests
{
    [Fact]
    public void Set_ThenGet_SameNormalizedCwd()
    {
        using var temp = new TempDirectoryScope("cli-map");
        var map = new CliSessionMap(new CliTestPathProvider(temp.Root));
        var cwd = Path.Combine(temp.Root, "proj");
        Directory.CreateDirectory(cwd);

        map.Set(cwd + Path.DirectorySeparatorChar, "session-1");
        Assert.Equal("session-1", map.Get(cwd));
    }

    [Fact]
    public void Set_OverwritesPreviousSession()
    {
        using var temp = new TempDirectoryScope("cli-map");
        var map = new CliSessionMap(new CliTestPathProvider(temp.Root));
        var cwd = Path.Combine(temp.Root, "proj");
        Directory.CreateDirectory(cwd);

        map.Set(cwd, "old");
        map.Set(cwd, "new");
        Assert.Equal("new", map.Get(cwd));
    }

    [Fact]
    public void Get_UnknownCwd_ReturnsNull()
    {
        using var temp = new TempDirectoryScope("cli-map");
        var map = new CliSessionMap(new CliTestPathProvider(temp.Root));
        Assert.Null(map.Get(Path.Combine(temp.Root, "missing")));
    }
}

internal sealed class CliTestPathProvider(string rootPath) : IAppPathProvider
{
    public string RootPath { get; } = rootPath;
    public string ConfigPath => Path.Combine(rootPath, "config");
    public string SessionsPath => Path.Combine(rootPath, "sessions");
    public string AuditPath => Path.Combine(rootPath, "audit");
    public string LogsPath => Path.Combine(rootPath, "logs");
    public string CredentialsPath => Path.Combine(rootPath, "credentials");
    public string SkillsPath => Path.Combine(rootPath, "skills");
    public void EnsureCreated() => Directory.CreateDirectory(rootPath);
    public string ResolveSkillPath(string path) =>
        Path.IsPathRooted(path) ? path : Path.Combine(SkillsPath, path);
}
