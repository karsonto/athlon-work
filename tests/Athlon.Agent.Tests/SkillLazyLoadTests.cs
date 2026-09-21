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

    [Fact]
    public void GetAllSkills_does_not_walk_resource_paths_eagerly()
    {
        // The recursive resource walk used to run during load even though its only consumer is the
        // "resource not found" message. On a session switch that cost ~10s across the catalog, so
        // the contract is now: loading a skill must not enumerate the skill's directory tree.
        var root = Path.Combine(Path.GetTempPath(), "athlon-skill-lazywalk", Guid.NewGuid().ToString("N"));
        var skillDir = Path.Combine(root, "walk-skill");
        Directory.CreateDirectory(skillDir);
        File.WriteAllText(
            Path.Combine(skillDir, SkillUtil.SkillFileName),
            """
            ---
            name: walk_skill
            description: Resource walk test
            ---
            Instructions only.
            """);
        Directory.CreateDirectory(Path.Combine(skillDir, "references"));
        File.WriteAllText(Path.Combine(skillDir, "references", "heavy.md"), "content");

        try
        {
            var catalog = new AgentSkillCatalog(new FileSystemSkillRepository(root));
            catalog.Reload();
            var skill = catalog.Skills.Single();

            // Deleting the directory proves nothing was snapshotted at load time: if the walk had
            // already happened, ResourcePaths would be populated and would still be non-empty.
            Directory.Delete(skillDir, recursive: true);

            Assert.Empty(skill.ResourcePaths);
            Assert.Empty(skill.Resources);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void ResourcePaths_are_cached_between_reads()
    {
        var root = Path.Combine(Path.GetTempPath(), "athlon-skill-lazycache", Guid.NewGuid().ToString("N"));
        var skillDir = Path.Combine(root, "cache-skill");
        Directory.CreateDirectory(Path.Combine(skillDir, "references"));
        File.WriteAllText(
            Path.Combine(skillDir, SkillUtil.SkillFileName),
            """
            ---
            name: cache_skill
            description: Resource cache test
            ---
            Instructions only.
            """);
        File.WriteAllText(Path.Combine(skillDir, "references", "one.md"), "a");

        try
        {
            var skill = SkillFileSystemHelper.LoadSkillFromDirectory(skillDir);
            var first = skill.ResourcePaths;
            Assert.Equal(new[] { "references/one.md" }, first);

            // A second read must not re-enumerate: otherwise every error message pays a fresh walk.
            File.WriteAllText(Path.Combine(skillDir, "references", "two.md"), "b");
            Assert.Same(first, skill.ResourcePaths);
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
