using Athlon.Agent.Core;
using Athlon.Agent.Infrastructure;
using Athlon.Agent.Skills;

namespace Athlon.Agent.Tests;

public sealed class SkillSettingsMergerTests
{
    [Fact]
    public void Merge_AddsNewSkillAsEnabledByDefault()
    {
        var root = CreateSkillRoot("new-skill", "new_skill", "New", "Body");
        try
        {
            var installed = SkillFileSystemHelper.GetAllSkills(root);
            var merged = SkillSettingsMerger.Merge(root, installed, []);

            Assert.Single(merged);
            Assert.Equal("new_skill", merged[0].Name);
            Assert.True(merged[0].Enabled);
            Assert.Equal("new-skill", merged[0].Path);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Merge_PreservesDisabledFlagFromSaved()
    {
        var root = CreateSkillRoot("skill-a", "skill_a", "A", "Body");
        try
        {
            var installed = SkillFileSystemHelper.GetAllSkills(root);
            var saved = new List<SkillSettings> { new() { Name = "skill_a", Enabled = false, Path = "skill-a" } };
            var merged = SkillSettingsMerger.Merge(root, installed, saved);

            Assert.Single(merged);
            Assert.False(merged[0].Enabled);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Merge_KeepsOrphanSavedSkillWhenRemovedFromDisk()
    {
        var saved = new List<SkillSettings> { new() { Name = "gone_skill", Enabled = false, Path = "gone" } };
        var merged = SkillSettingsMerger.Merge(Path.Combine(Path.GetTempPath(), "missing-skills"), [], saved);

        Assert.Single(merged);
        Assert.Equal("gone_skill", merged[0].Name);
        Assert.False(merged[0].Enabled);
    }

    private static string CreateSkillRoot(string folderName, string name, string description, string body)
    {
        var root = Path.Combine(Path.GetTempPath(), $"athlon-skill-merge-{Guid.NewGuid():N}");
        var skillDir = Path.Combine(root, folderName);
        Directory.CreateDirectory(skillDir);
        File.WriteAllText(
            Path.Combine(skillDir, SkillUtil.SkillFileName),
            $"""
            ---
            name: {name}
            description: {description}
            ---
            {body}
            """);
        return root;
    }
}
