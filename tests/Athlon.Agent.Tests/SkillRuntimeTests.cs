using Athlon.Agent.Core;
using Athlon.Agent.Infrastructure;
using Athlon.Agent.Skills;
using Athlon.Agent.Skills.Repository;

namespace Athlon.Agent.Tests;

public sealed class SkillRuntimeTests
{
    [Fact]
    public void AgentSkillCatalog_GetSkillById_MatchesName()
    {
        var root = CreateSkillRoot("by-id-skill", "demo_skill", "Demo", "Body.");
        try
        {
            var catalog = new AgentSkillCatalog(new FileSystemSkillRepository(root));
            catalog.Reload();
            var loaded = catalog.Skills.Single();

            var skill = catalog.GetSkillById(loaded.SkillId);
            Assert.NotNull(skill);
            Assert.Equal("demo_skill", skill!.Name);
            Assert.Same(loaded, skill);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void GetSkills_ExcludesDisabledSkillsFromSettings()
    {
        var root = Path.Combine(Path.GetTempPath(), "athlon-skill-runtime", Guid.NewGuid().ToString("N"));
        CreateSkillFolder(root, "enabled-skill", "enabled_skill", "Enabled", "Enabled body.");
        CreateSkillFolder(root, "disabled-skill", "disabled_skill", "Disabled", "Disabled body.");
        try
        {
            var catalog = new AgentSkillCatalog(new FileSystemSkillRepository(root));
            var settings = new AppSettings
            {
                Skills = { new SkillSettings { Name = "disabled_skill", Enabled = false } }
            };
            var runtime = new SkillRuntime(catalog, settings);

            var skills = runtime.GetSkills();
            Assert.Single(skills);
            Assert.Equal("enabled_skill", skills[0].Name);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void LoadResource_SkillMd_ActivatesSkillAndReturnsContent()
    {
        var root = CreateSkillRoot("load-skill", "load_skill", "Load", "Instruction body.");
        try
        {
            var catalog = new AgentSkillCatalog(new FileSystemSkillRepository(root));
            catalog.Reload();
            var skillId = catalog.Skills.Single().SkillId;
            var runtime = new SkillRuntime(catalog, new AppSettings());

            using (SessionSkillActivationScope.EnterNewTurn())
            {
                var content = runtime.LoadResource(skillId, "SKILL.md");
                var expectedFilesRoot = ToolPathNormalizer.ForModel(Path.GetFullPath(Path.Combine(root, "load-skill")));
                Assert.Contains($"Successfully loaded skill: {skillId}", content, StringComparison.Ordinal);
                Assert.Contains($"Files root: {expectedFilesRoot}", content, StringComparison.Ordinal);
                Assert.Contains("Instruction body.", content, StringComparison.Ordinal);
                Assert.True(runtime.IsActive(skillId));
            }
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void LoadResource_InvalidPath_ListsAvailableResources()
    {
        var root = CreateSkillRoot("res-skill", "res_skill", "Res", "Body.");
        var skillDir = Path.Combine(root, "res-skill");
        var referencesDir = Path.Combine(skillDir, "references");
        Directory.CreateDirectory(referencesDir);
        File.WriteAllText(Path.Combine(referencesDir, "guide.md"), "# Guide");
        try
        {
            var catalog = new AgentSkillCatalog(new FileSystemSkillRepository(root));
            catalog.Reload();
            var skillId = catalog.Skills.Single().SkillId;
            var runtime = new SkillRuntime(catalog, new AppSettings());

            var ex = Assert.Throws<ArgumentException>(() =>
                runtime.LoadResource(skillId, "missing.md"));

            Assert.Contains("Available resources:", ex.Message, StringComparison.Ordinal);
            Assert.Contains("SKILL.md", ex.Message, StringComparison.Ordinal);
            Assert.Contains("references/guide.md", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task LoadSkillThroughPathTool_Invoke_ReturnsSkillContent()
    {
        var root = CreateSkillRoot("tool-skill", "tool_skill", "Tool", "Tool instructions.");
        try
        {
            var catalog = new AgentSkillCatalog(new FileSystemSkillRepository(root));
            catalog.Reload();
            var skillId = catalog.Skills.Single().SkillId;
            var runtime = new SkillRuntime(catalog, new AppSettings());
            var tool = new LoadSkillThroughPathTool(runtime);

            using (SessionSkillActivationScope.EnterNewTurn())
            {
                var result = await tool.InvokeAsync(new ToolInvocation(
                    "load_skill_through_path",
                    new Dictionary<string, string>
                    {
                        ["skillId"] = skillId,
                        ["path"] = "SKILL.md"
                    }));

                var expectedFilesRoot = ToolPathNormalizer.ForModel(Path.GetFullPath(Path.Combine(root, "tool-skill")));
                Assert.True(result.Succeeded);
                Assert.Contains($"Files root: {expectedFilesRoot}", result.Content, StringComparison.Ordinal);
                Assert.Contains("Tool instructions.", result.Content, StringComparison.Ordinal);
            }
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void AgentEnvironmentPromptBuilder_IncludesLoadSkillGuidance_WhenSkillsPresent()
    {
        var root = CreateSkillRoot("prompt-skill", "demo_skill", "Demo skill", "Instructions.");
        try
        {
            var catalog = new AgentSkillCatalog(new FileSystemSkillRepository(root));
            catalog.Reload();
            var builder = PromptTestHelpers.CreateBuilder(
                new PromptTestHelpers.FakeHostEnvironment(@"C:\Users\test\.athlon-agent\skills", @"C:\Users\test\.athlon-agent"),
                catalog: catalog);

            var prompt = builder.Build(AgentSession.Create("skill-prompt"), Array.Empty<ToolDefinition>());

            Assert.Contains("## Available Skills", prompt, StringComparison.Ordinal);
            Assert.Contains("<available_skills>", prompt, StringComparison.Ordinal);
            Assert.Contains("<skill-id>demo_skill</skill-id>", prompt, StringComparison.Ordinal);
            Assert.Contains("load_skill_through_path", prompt, StringComparison.Ordinal);
            Assert.Contains("<files-root>", prompt, StringComparison.Ordinal);
            Assert.Contains("## Code Execution", prompt, StringComparison.Ordinal);
            Assert.Contains("Skill scripts:", prompt, StringComparison.Ordinal);
            Assert.DoesNotContain("skills directory root as path", prompt, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("- file_read:", prompt, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void SkillComposerExpander_ExpandsKnownSkillReference()
    {
        var expanded = SkillComposerExpander.Expand(
            "Please use @skill:demo_skill for this task.",
            [new AvailableSkillInfo("demo_skill", "Demo", "demo_skill")]);

        Assert.Contains("[Skill reference: demo_skill]", expanded, StringComparison.Ordinal);
        Assert.Contains("load_skill_through_path(skillId=\"demo_skill\"", expanded, StringComparison.Ordinal);
        Assert.Contains("@skill:demo_skill", expanded, StringComparison.Ordinal);
    }

    [Fact]
    public void SkillComposerExpander_AppendsWarningForUnknownSkill()
    {
        var expanded = SkillComposerExpander.Expand(
            "@skill:missing_skill",
            Array.Empty<AvailableSkillInfo>());

        Assert.Contains("Unknown skill 'missing_skill'", expanded, StringComparison.Ordinal);
    }

    private static string CreateSkillRoot(string folderName, string name, string description, string body)
    {
        var root = Path.Combine(Path.GetTempPath(), "athlon-skill-runtime", Guid.NewGuid().ToString("N"));
        CreateSkillFolder(root, folderName, name, description, body);
        return root;
    }

    private static void CreateSkillFolder(
        string root,
        string folderName,
        string name,
        string description,
        string body)
    {
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
    }

}
