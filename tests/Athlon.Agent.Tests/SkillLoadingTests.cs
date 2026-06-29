using Athlon.Agent.Skills;
using Athlon.Agent.Skills.Repository;

namespace Athlon.Agent.Tests;

public sealed class SkillLoadingTests
{
    [Fact]
    public void MarkdownSkillParser_ParsesFrontmatterAndBody()
    {
        const string markdown = """
            ---
            name: data_cleaning
            description: Clean tabular data
            version: 1.0.0
            ---
            # Instructions
            Use pandas when needed.
            """;

        var parsed = MarkdownSkillParser.Parse(markdown);

        Assert.Equal("data_cleaning", parsed.Metadata["name"]);
        Assert.Equal("Clean tabular data", parsed.Metadata["description"]);
        Assert.Contains("# Instructions", parsed.Content, StringComparison.Ordinal);
    }

    [Fact]
    public void FileSystemSkillRepository_LoadsSkillFolder()
    {
        var root = Path.Combine(Path.GetTempPath(), "athlon-skill-tests", Guid.NewGuid().ToString("N"));
        var skillDir = Path.Combine(root, "sample-skill");
        Directory.CreateDirectory(skillDir);

        File.WriteAllText(
            Path.Combine(skillDir, SkillUtil.SkillFileName),
            """
            ---
            name: sample_skill
            description: Sample skill for tests
            ---
            Follow these steps.
            """);

        var referencesDir = Path.Combine(skillDir, "references");
        Directory.CreateDirectory(referencesDir);
        File.WriteAllText(Path.Combine(referencesDir, "guide.md"), "# Guide");

        try
        {
            var repo = new FileSystemSkillRepository(root);
            var skills = repo.GetAllSkills();

            Assert.Single(skills);
            Assert.Equal("sample_skill", skills[0].Name);
            Assert.Equal("Sample skill for tests", skills[0].Description);
            Assert.Contains("Follow these steps.", skills[0].SkillContent, StringComparison.Ordinal);
            Assert.NotNull(repo.GetSkill("sample_skill"));
            var catalog = new AgentSkillCatalog(repo);
            catalog.Reload();
            var byId = catalog.GetSkillById(skills[0].SkillId);
            Assert.NotNull(byId);
            Assert.Equal(skills[0].SkillId, byId!.SkillId);
            Assert.Equal(skills[0].Name, byId.Name);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [Fact]
    public void FileSystemSkillRepository_LogsSkillLoadFailures()
    {
        var root = Path.Combine(Path.GetTempPath(), "athlon-skill-tests", Guid.NewGuid().ToString("N"));
        var validDir = Path.Combine(root, "valid-skill");
        var invalidDir = Path.Combine(root, "invalid-skill");
        Directory.CreateDirectory(validDir);
        Directory.CreateDirectory(invalidDir);

        File.WriteAllText(
            Path.Combine(validDir, SkillUtil.SkillFileName),
            """
            ---
            name: valid_skill
            description: Valid skill for tests
            ---
            Follow these steps.
            """);

        File.WriteAllText(
            Path.Combine(invalidDir, SkillUtil.SkillFileName),
            """
            ---
            name: invalid_skill
            ---
            Missing description frontmatter.
            """);

        var failures = new List<(string Dir, Exception Exception)>();
        try
        {
            var repo = new FileSystemSkillRepository(
                root,
                (dir, ex) => failures.Add((dir, ex)));
            var skills = repo.GetAllSkills();

            Assert.Single(skills);
            Assert.Equal("valid_skill", skills[0].Name);
            Assert.Single(failures);
            Assert.Equal(invalidDir, failures[0].Dir, ignoreCase: true);
            Assert.Contains("description", failures[0].Exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }
}
