using Athlon.Agent.Core;
using Athlon.Agent.Infrastructure;
using Athlon.Agent.Skills;
using Athlon.Agent.Skills.Repository;

namespace Athlon.Agent.Tests;

public sealed class SkillLazyLoadTests
{
    [Fact]
    public void GetAllSkills_does_not_load_resource_file_contents()
    {
        var root = Path.Combine(Path.GetTempPath(), "athlon-skill-lazy", Guid.NewGuid().ToString("N"));
        var skillDir = Path.Combine(root, "lazy-skill");
        Directory.CreateDirectory(skillDir);
        File.WriteAllText(
            Path.Combine(skillDir, SkillUtil.SkillFileName),
            """
            ---
            name: lazy_skill
            description: Lazy load test
            ---
            Instructions only.
            """);

        var referencesDir = Path.Combine(skillDir, "references");
        Directory.CreateDirectory(referencesDir);
        var heavyPath = Path.Combine(referencesDir, "heavy.md");
        File.WriteAllText(heavyPath, new string('x', 500_000));

        try
        {
            var catalog = new AgentSkillCatalog(new FileSystemSkillRepository(root));
            catalog.Reload();
            var skill = catalog.Skills.Single();

            Assert.Empty(skill.Resources);
            Assert.Contains("references/heavy.md", skill.ResourcePaths);
            Assert.True(skill.SupportsLazyResourceLoad);

            var runtime = new SkillRuntime(catalog, new AppSettings());
            using (SessionSkillActivationScope.EnterNewTurn())
            {
                var content = runtime.LoadResource(skill.SkillId, "references/heavy.md");
                Assert.Contains('x', content);
            }
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
