using Athlon.Agent.Core;
using Athlon.Agent.Infrastructure;

namespace Athlon.Agent.Tests;

public sealed class ToolCatalogFingerprintTests
{
    [Fact]
    public void LoadSkillTool_definition_and_fingerprint_do_not_depend_on_dynamic_skill_catalog()
    {
        var first = new LoadSkillThroughPathTool(new StubSkillRuntime(
            [new AvailableSkillInfo("first", "First", "first")]));
        var second = new LoadSkillThroughPathTool(new StubSkillRuntime(
            [new AvailableSkillInfo("second", "Second", "second")]));

        Assert.Equal(first.Definition, second.Definition);
        Assert.DoesNotContain("first", first.Definition.Description, StringComparison.Ordinal);
        Assert.DoesNotContain("second", second.Definition.Description, StringComparison.Ordinal);
        Assert.Equal(
            ToolCatalogFingerprint.Compute([first.Definition]),
            ToolCatalogFingerprint.Compute([second.Definition]));
    }

    private sealed class StubSkillRuntime(IReadOnlyList<AvailableSkillInfo> skills) : ISkillRuntime
    {
        public IReadOnlyList<AvailableSkillInfo> GetSkills() => skills;
        public string LoadResource(string skillId, string path) => string.Empty;
        public void Activate(string skillId) { }
        public bool IsActive(string skillId) => false;
    }
}
