using System.Text;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Prompt;
using Athlon.Agent.Infrastructure.Prompt;

namespace Athlon.Agent.Tests;

public sealed class HostWorkspaceRuntimeContributorTests
{
    [Fact]
    public void Append_IncludesHostUserSkillsAndWorkspaceIdentity()
    {
        var skillsPath = @"C:\Users\test\.athlon-agent\skills";
        var context = new EnvironmentPromptContext
        {
            Session = AgentSession.Create("runtime-host"),
            WorkspaceRoot = @"C:\work\demo",
            WorkspaceName = "demo",
            WorkspaceKind = WorkspaceKind.Local,
            Tools = Array.Empty<ToolDefinition>(),
            SkillsDirectory = skillsPath,
            Host = new PromptTestHelpers.FakeHostEnvironment(skillsPath, @"C:\Users\test\.athlon-agent"),
            PromptSettings = new PromptSettings()
        };

        var builder = new StringBuilder();
        new HostWorkspaceRuntimeContributor().Append(builder, context);
        var text = builder.ToString();

        Assert.Contains(@"Host user: TESTDOMAIN\karson", text, StringComparison.Ordinal);
        Assert.Contains($"Skills directory: {skillsPath}", text, StringComparison.Ordinal);
        Assert.Contains("Workspace kind: local", text, StringComparison.Ordinal);
        Assert.Contains("Workspace name: demo", text, StringComparison.Ordinal);
        Assert.Contains(@"Workspace root: C:\work\demo", text, StringComparison.Ordinal);
        Assert.DoesNotContain("signed-in user", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Append_IncludesSignedInUser_WhenSsoDisplayNamePresent()
    {
        var skillsPath = @"C:\Users\test\.athlon-agent\skills";
        var context = new EnvironmentPromptContext
        {
            Session = AgentSession.Create("runtime-sso"),
            Tools = Array.Empty<ToolDefinition>(),
            SkillsDirectory = skillsPath,
            Host = new PromptTestHelpers.FakeHostEnvironment(skillsPath, @"C:\Users\test\.athlon-agent"),
            PromptSettings = new PromptSettings(),
            SsoUserDisplayName = "Zhang San"
        };

        var builder = new StringBuilder();
        new HostWorkspaceRuntimeContributor().Append(builder, context);
        var text = builder.ToString();

        Assert.Contains("The signed-in user is Zhang San.", text, StringComparison.Ordinal);
        Assert.Contains("Address them by name when appropriate.", text, StringComparison.Ordinal);
        Assert.Contains(@"Host user: TESTDOMAIN\karson", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Append_SkipsSignedInUser_WhenSsoDisplayNameMissing()
    {
        var skillsPath = @"C:\Users\test\.athlon-agent\skills";
        var context = new EnvironmentPromptContext
        {
            Session = AgentSession.Create("runtime-no-sso"),
            Tools = Array.Empty<ToolDefinition>(),
            SkillsDirectory = skillsPath,
            Host = new PromptTestHelpers.FakeHostEnvironment(skillsPath, @"C:\Users\test\.athlon-agent"),
            PromptSettings = new PromptSettings(),
            SsoUserDisplayName = null
        };

        var builder = new StringBuilder();
        new HostWorkspaceRuntimeContributor().Append(builder, context);
        var text = builder.ToString();

        Assert.DoesNotContain("signed-in user", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(@"Host user: TESTDOMAIN\karson", text, StringComparison.Ordinal);
    }
}
