using Athlon.Agent.Core;

namespace Athlon.Agent.Tests;

public sealed class AgentEnvironmentPromptBuilderTests
{
    [Fact]
    public void Build_IncludesWindowsHostEnvironmentAndSkillsDirectory()
    {
        var skillsPath = @"C:\Users\test\.athlon-agent\skills";
        var appDataPath = @"C:\Users\test\.athlon-agent";
        var builder = new AgentEnvironmentPromptBuilder(
            new AppSettings(),
            new FixedSkillsProvider(),
            new FakeHostEnvironment(skillsPath, appDataPath));

        var prompt = builder.Build(AgentSession.Create("prompt-test"), Array.Empty<ToolDefinition>());

        Assert.Contains("Host environment (current Windows user session):", prompt, StringComparison.Ordinal);
        Assert.Contains(@"User: TESTDOMAIN\karson", prompt, StringComparison.Ordinal);
        Assert.Contains($"- Default skills directory: {skillsPath}", prompt, StringComparison.Ordinal);
        Assert.Contains($"- Agent app data: {appDataPath}", prompt, StringComparison.Ordinal);
        Assert.Contains($"none installed under {skillsPath}", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("~/.athlon-agent/skills", prompt, StringComparison.Ordinal);
    }

    private sealed class FixedSkillsProvider : IAvailableSkillsProvider
    {
        public IReadOnlyList<AvailableSkillInfo> GetSkills() => Array.Empty<AvailableSkillInfo>();
    }

    private sealed class FakeHostEnvironment(string skillsDirectory, string appDataDirectory) : IAgentHostEnvironment
    {
        public bool IsWindows => true;
        public string OsDescription => "Microsoft Windows 11";
        public string OsVersion => "10.0.22631.0";
        public string UserName => "karson";
        public string UserDomainName => "TESTDOMAIN";
        public string MachineName => "DESKTOP-TEST";
        public string UserProfilePath => @"C:\Users\karson";
        public string CurrentDirectory => @"C:\Users\karson\athlon-work";
        public string SystemDirectory => @"C:\Windows\system32";
        public string ProcessArchitecture => "X64";
        public string OsArchitecture => "X64";
        public int ProcessorCount => 8;
        public string AppDataDirectory { get; } = appDataDirectory;
        public string SkillsDirectory { get; } = skillsDirectory;
    }
}
