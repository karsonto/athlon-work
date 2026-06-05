using System.Text;
using Athlon.Agent.Core;
using Athlon.Agent.Skills;

namespace Athlon.Agent.Tests;

public sealed class SkillXmlPromptRendererTests
{
    [Fact]
    public void AppendSkillPrompt_RendersCoreXmlFields()
    {
        var skill = new AgentSkill(
            new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["name"] = "demo_skill",
                ["description"] = "Demo & test"
            },
            "Body",
            resources: null);

        var builder = new StringBuilder();
        SkillXmlPromptRenderer.AppendSkillPrompt(builder, [skill]);

        var text = builder.ToString();
        Assert.Contains("<available_skills>", text, StringComparison.Ordinal);
        Assert.Contains("<skill>", text, StringComparison.Ordinal);
        Assert.Contains("<name>demo_skill</name>", text, StringComparison.Ordinal);
        Assert.Contains("<description>Demo &amp; test</description>", text, StringComparison.Ordinal);
        Assert.Contains("<skill-id>demo_skill</skill-id>", text, StringComparison.Ordinal);
        Assert.Contains("load_skill_through_path", text, StringComparison.Ordinal);
        Assert.DoesNotContain("<files-root>", text, StringComparison.Ordinal);
        Assert.DoesNotContain("## Code Execution", text, StringComparison.Ordinal);
        Assert.DoesNotContain("skills directory root as path", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AppendSkillPrompt_RendersFilesRootAndCodeExecution_WhenSkillDirectoryExists()
    {
        var skillDir = Path.Combine(Path.GetTempPath(), "athlon-skill-xml", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(skillDir);
        try
        {
            var expectedFilesRoot = ToolPathNormalizer.ForModel(Path.GetFullPath(skillDir));
            var skill = new AgentSkill(
                new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["name"] = "demo_skill",
                    ["description"] = "Demo"
                },
                "Body",
                skillDirectory: skillDir);

            var builder = new StringBuilder();
            SkillXmlPromptRenderer.AppendSkillPrompt(builder, [skill]);

            var text = builder.ToString();
            Assert.Contains($"<files-root>{expectedFilesRoot}</files-root>", text, StringComparison.Ordinal);
            Assert.Contains("## Code Execution", text, StringComparison.Ordinal);
            Assert.Contains("<code_execution>", text, StringComparison.Ordinal);
            Assert.Contains("execute_command", text, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(skillDir, true);
        }
    }

    [Fact]
    public void AppendSkillPrompt_DoesNothing_WhenEmpty()
    {
        var builder = new StringBuilder();
        SkillXmlPromptRenderer.AppendSkillPrompt(builder, Array.Empty<AgentSkill>());
        Assert.Equal(string.Empty, builder.ToString());
    }
}
