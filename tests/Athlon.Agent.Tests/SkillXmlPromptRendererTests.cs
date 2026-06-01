using System.Text;
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
    }

    [Fact]
    public void AppendSkillPrompt_DoesNothing_WhenEmpty()
    {
        var builder = new StringBuilder();
        SkillXmlPromptRenderer.AppendSkillPrompt(builder, Array.Empty<AgentSkill>());
        Assert.Equal(string.Empty, builder.ToString());
    }
}
