using Athlon.Agent.App.Services;
using Athlon.Agent.App.ViewModels;
using Athlon.Agent.Core;
using Athlon.Agent.Skills;

namespace Athlon.Agent.Tests;

public sealed class ComposerAtCompletionServiceTests
{
    [Fact]
    public async Task FilterMatches_empty_query_returns_skills_before_indexed_files()
    {
        var workspace = Path.Combine(Path.GetTempPath(), "composer-at-completion-order-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        await File.WriteAllTextAsync(Path.Combine(workspace, "beta.txt"), "beta");

        var service = new ComposerAtCompletionService();
        var updates = 0;
        var updated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        service.SourcesUpdated += () =>
        {
            if (Interlocked.Increment(ref updates) >= 2)
            {
                updated.TrySetResult();
            }
        };

        service.RefreshSources(
            new StubSkillCatalog([CreateSkill("alpha-skill")]),
            new AppSettings { Skills = [new SkillSettings { Name = "alpha-skill", Enabled = true }] },
            workspace,
            []);

        await updated.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var matches = service.FilterMatches(string.Empty);
        Assert.True(matches.Count >= 2);
        Assert.Equal("alpha-skill", matches[0].PrimaryText);
        Assert.Contains(matches, item => item.PrimaryText == "beta.txt");
    }

    [Fact]
    public async Task RefreshSources_skips_ignored_directories_when_indexing_files()
    {
        var workspace = Path.Combine(Path.GetTempPath(), "composer-at-completion-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(workspace, "src"));
        Directory.CreateDirectory(Path.Combine(workspace, "node_modules", "pkg"));
        await File.WriteAllTextAsync(Path.Combine(workspace, "src", "app.cs"), "class App {}");
        await File.WriteAllTextAsync(Path.Combine(workspace, "node_modules", "pkg", "ignored.js"), "ignored");

        var service = new ComposerAtCompletionService();
        var updates = 0;
        var updated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        service.SourcesUpdated += () =>
        {
            if (Interlocked.Increment(ref updates) >= 2)
            {
                updated.TrySetResult();
            }
        };

        service.RefreshSources(
            new EmptySkillCatalog(),
            new AppSettings(),
            workspace,
            ["node_modules"]);

        await updated.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var matches = service.FilterMatches(string.Empty);
        Assert.Contains(matches, item => item.PrimaryText == "app.cs");
        Assert.DoesNotContain(matches, item => item.PrimaryText == "ignored.js");
    }

    private static AgentSkill CreateSkill(string name) =>
        new(
            new Dictionary<string, object>
            {
                ["name"] = name,
                ["description"] = $"{name} description"
            },
            $"# {name}");

    private sealed class EmptySkillCatalog : IAgentSkillCatalog
    {
        public IReadOnlyList<AgentSkill> Skills { get; } = Array.Empty<AgentSkill>();

        public AgentSkill? GetSkill(string name) => null;

        public AgentSkill? GetSkillById(string skillId) => null;

        public void Reload()
        {
        }
    }

    private sealed class StubSkillCatalog(IReadOnlyList<AgentSkill> skills) : IAgentSkillCatalog
    {
        public IReadOnlyList<AgentSkill> Skills { get; } = skills;

        public AgentSkill? GetSkill(string name) =>
            Skills.FirstOrDefault(skill => string.Equals(skill.Name, name, StringComparison.Ordinal));

        public AgentSkill? GetSkillById(string skillId) => GetSkill(skillId);

        public void Reload()
        {
        }
    }
}
