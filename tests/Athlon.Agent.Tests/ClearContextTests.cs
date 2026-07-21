using Athlon.Agent.Core;

namespace Athlon.Agent.Tests;

public sealed class ClearContextTests
{
    [Fact]
    public void BuildModelMessages_EmptyHistory_ContainsOnlySystem()
    {
        var messages = AgentRuntime.BuildModelMessages("system-prompt", Array.Empty<ChatMessage>());

        Assert.Single(messages);
        Assert.Equal("system", messages[0].Role);
        Assert.Equal("system-prompt", messages[0].Content);
    }

    [Fact]
    public void BuildModelMessages_AfterClearThenNewUser_IncludesSystemAndUser()
    {
        var createdAt = new DateTimeOffset(2026, 6, 30, 14, 30, 0, TimeSpan.Zero);
        var history = new[]
        {
            new ChatMessage("user-1", MessageRole.User, "fresh start", createdAt)
        };

        var messages = AgentRuntime.BuildModelMessages("system-prompt", history);

        Assert.Equal(2, messages.Count);
        Assert.Equal("system", messages[0].Role);
        Assert.Equal("user", messages[1].Role);
        var content = Assert.IsType<string>(messages[1].Content);
        Assert.Contains("fresh start", content, StringComparison.Ordinal);
        Assert.EndsWith("[2026-06-30 22:30 UTC+8]", content, StringComparison.Ordinal);
    }

    [Fact]
    public void EnvironmentPromptBuilder_AfterClearingMessages_StillIncludesWorkspaceAndTools()
    {
        var workspaceRoot = Path.Combine(Path.GetTempPath(), "athlon-clear-ctx", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspaceRoot);

        try
        {
            var settings = new AppSettings
            {
                Workspaces =
                [
                    new WorkspaceSettings
                    {
                        Name = "test-ws",
                        RootPath = workspaceRoot
                    }
                ]
            };

            var session = AgentSession.Create("cleared")
                .WithWorkspace(workspaceRoot)
                .WithMessages(Array.Empty<ChatMessage>());

            var tools = new[]
            {
                new ToolDefinition("file_read", "Read a file", ToolSchema.Object().Build())
            };

            var builder = PromptTestHelpers.CreateBuilder(new MinimalHostEnvironment(), settings);

            var prompt = builder.Build(session, tools);

            Assert.DoesNotContain($"Workspace root: {workspaceRoot}", prompt, StringComparison.Ordinal);
            Assert.Contains("not a path prefix", prompt, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Native tools via function calling", prompt, StringComparison.Ordinal);
            Assert.DoesNotContain("- file_read: Read a file", prompt, StringComparison.Ordinal);
            Assert.Contains("You are Athlon Agent", prompt, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(Path.GetDirectoryName(workspaceRoot)!))
            {
                Directory.Delete(Path.GetDirectoryName(workspaceRoot)!, true);
            }
        }
    }

    private sealed class MinimalHostEnvironment : IAgentHostEnvironment
    {
        public bool IsWindows => true;
        public string OsDescription => "Test OS";
        public string OsVersion => "1.0";
        public string UserName => "user";
        public string UserDomainName => "DOMAIN";
        public string MachineName => "MACHINE";
        public string UserProfilePath => @"C:\Users\user";
        public string SystemDirectory => @"C:\Windows\system32";
        public string ProcessArchitecture => "X64";
        public string OsArchitecture => "X64";
        public int ProcessorCount => 4;
        public string AppDataDirectory => @"C:\Users\user\.athlon-agent";
        public string SkillsDirectory => @"C:\Users\user\.athlon-agent\skills";
    }
}
