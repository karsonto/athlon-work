using System.Collections.ObjectModel;
using Athlon.Agent.App.Services;
using Athlon.Agent.App.Services.SlashCommands;
using Athlon.Agent.App.ViewModels;
using Athlon.Agent.Core;
using Athlon.Agent.Infrastructure;
using Athlon.Agent.Skills;
using Athlon.Agent.Skills.Repository;

namespace Athlon.Agent.Tests;

public sealed class ComposerCoordinatorTests
{
    [Fact]
    public void TryAcceptAtCompletion_ReplacesAtQuerySpan()
    {
        var skillRoot = Path.Combine(Path.GetTempPath(), "composer-coord-skills-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(skillRoot);
        var coordinator = ComposerTestFactory.CreateCoordinator(
            new AgentSkillCatalog(new FileSystemSkillRepository(skillRoot)));

        var items = new ObservableCollection<AtCompletionItemViewModel>
        {
            new("文件", "readme.md", "/docs/readme.md", "@readme.md", "readme.md")
        };

        var composerText = "see @rea";
        var accepted = coordinator.TryAcceptAtCompletion(
            composerText,
            caretIndex: composerText.Length,
            isOpen: true,
            selectedIndex: 0,
            items,
            text => composerText = text,
            () => { },
            slashContext: null,
            out var newCaret);

        Assert.True(accepted);
        Assert.Contains("readme.md", composerText);
        Assert.True(newCaret > 0);
    }

    [Fact]
    public void TryAcceptAtCompletion_Folder_InsertsTrailingSlash()
    {
        var coordinator = ComposerTestFactory.CreateCoordinator();
        var items = new ObservableCollection<AtCompletionItemViewModel>
        {
            new(
                "文件夹",
                "src",
                "src/",
                "@src/",
                "src",
                ComposerCompletionItemKind.Folder,
                IconKind: WorkspaceFileIconKind.Folder)
        };

        var composerText = "open @sr";
        var accepted = coordinator.TryAcceptAtCompletion(
            composerText,
            caretIndex: composerText.Length,
            isOpen: true,
            selectedIndex: 0,
            items,
            text => composerText = text,
            () => { },
            slashContext: null,
            out _);

        Assert.True(accepted);
        Assert.Contains("@src/ ", composerText);
    }

    [Fact]
    public async Task UpdateAtCompletion_At_IncludesFoldersFromWorkspace()
    {
        var workspace = Path.Combine(Path.GetTempPath(), "composer-folder-workspace-" + Guid.NewGuid().ToString("N"));
        var nestedDir = Path.Combine(workspace, "feature-dir");
        Directory.CreateDirectory(nestedDir);
        await File.WriteAllTextAsync(Path.Combine(nestedDir, "a.txt"), "hi");

        try
        {
            var service = ComposerTestFactory.CreateCompletionService();
            var coordinator = ComposerTestFactory.CreateCoordinator(completionService: service);
            var indexCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var updateCount = 0;
            service.SourcesUpdated += () =>
            {
                if (Interlocked.Increment(ref updateCount) >= 2)
                {
                    indexCompleted.TrySetResult();
                }
            };

            service.EnsureFileIndexBuilt(
                new ComposerTestFactory.StubSkillCatalog([]),
                new AppSettings(),
                workspace,
                ignorePatterns: [".git"]);

            await indexCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));

            var items = new ObservableCollection<AtCompletionItemViewModel>();
            var isOpen = false;
            coordinator.UpdateAtCompletion(
                "@feature",
                caretIndex: "@feature".Length,
                activeWorkspace: workspace,
                ignorePatterns: [".git"],
                items,
                open => isOpen = open,
                _ => { },
                -1);

            Assert.True(isOpen);
            var folder = Assert.Single(items, item => item.Kind == ComposerCompletionItemKind.Folder);
            Assert.Equal("feature-dir", folder.PrimaryText);
            Assert.Equal("@feature-dir/", folder.InsertText);
            Assert.Equal(WorkspaceFileIconKind.Folder, folder.IconKind);
            Assert.Contains(items, item => item.Kind == ComposerCompletionItemKind.File && item.PrimaryText == "a.txt");
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    [Fact]
    public async Task UpdateAtCompletion_At_IgnoresConfiguredDirectories()
    {
        var workspace = Path.Combine(Path.GetTempPath(), "composer-ignore-workspace-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(workspace, ".git"));
        Directory.CreateDirectory(Path.Combine(workspace, "keep-dir"));
        await File.WriteAllTextAsync(Path.Combine(workspace, "keep-dir", "b.txt"), "x");

        try
        {
            var service = ComposerTestFactory.CreateCompletionService();
            var coordinator = ComposerTestFactory.CreateCoordinator(completionService: service);
            var indexCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var updateCount = 0;
            service.SourcesUpdated += () =>
            {
                if (Interlocked.Increment(ref updateCount) >= 2)
                {
                    indexCompleted.TrySetResult();
                }
            };

            service.EnsureFileIndexBuilt(
                new ComposerTestFactory.StubSkillCatalog([]),
                new AppSettings(),
                workspace,
                ignorePatterns: [".git"]);

            await indexCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));

            var items = new ObservableCollection<AtCompletionItemViewModel>();
            coordinator.UpdateAtCompletion(
                "@",
                caretIndex: 1,
                activeWorkspace: workspace,
                ignorePatterns: [".git"],
                items,
                _ => { },
                _ => { },
                -1);

            Assert.DoesNotContain(items, item => item.PrimaryText == ".git");
            Assert.Contains(items, item => item.Kind == ComposerCompletionItemKind.Folder && item.PrimaryText == "keep-dir");
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    [Fact]
    public void CloseAtCompletion_ClearsItemsAndResetsSelection()
    {
        var coordinator = ComposerTestFactory.CreateCoordinator();
        var items = new ObservableCollection<AtCompletionItemViewModel>
        {
            new("文件", "a.txt", "a.txt", "@a.txt", "a.txt")
        };
        var isOpen = true;
        var selected = 0;

        coordinator.CloseAtCompletion(items, open => isOpen = open, index => selected = index);

        Assert.False(isOpen);
        Assert.Equal(-1, selected);
        Assert.Empty(items);
    }

    [Fact]
    public void UpdateAtCompletion_Slash_OnlyShowsEnabledSkills()
    {
        var settings = new AppSettings
        {
            Skills =
            [
                new SkillSettings { Name = "enabled-skill", Enabled = true },
                new SkillSettings { Name = "disabled-skill", Enabled = false }
            ]
        };
        var coordinator = ComposerTestFactory.CreateCoordinator(
            new ComposerTestFactory.StubSkillCatalog(
            [
                CreateSkill("enabled-skill"),
                CreateSkill("disabled-skill")
            ]),
            settings);

        var items = new ObservableCollection<AtCompletionItemViewModel>();
        var isOpen = false;
        var selected = -1;

        coordinator.UpdateAtCompletion(
            "/",
            caretIndex: 1,
            activeWorkspace: null,
            ignorePatterns: [],
            items,
            open => isOpen = open,
            index => selected = index,
            selected);

        Assert.True(isOpen);
        Assert.Equal(0, selected);
        Assert.Contains(items, item => item.PrimaryText == "enabled-skill");
        Assert.DoesNotContain(items, item => item.PrimaryText == "disabled-skill");
    }

    [Fact]
    public void UpdateAtCompletion_At_DoesNotShowSkills()
    {
        var settings = new AppSettings
        {
            Skills = [new SkillSettings { Name = "enabled-skill", Enabled = true }]
        };
        var coordinator = ComposerTestFactory.CreateCoordinator(
            new ComposerTestFactory.StubSkillCatalog([CreateSkill("enabled-skill")]),
            settings);

        var items = new ObservableCollection<AtCompletionItemViewModel>();
        var isOpen = false;

        coordinator.UpdateAtCompletion(
            "@",
            caretIndex: 1,
            activeWorkspace: null,
            ignorePatterns: [],
            items,
            open => isOpen = open,
            _ => { },
            -1);

        Assert.False(isOpen);
        Assert.Empty(items);
    }

    [Fact]
    public void UpdateAtCompletion_Slash_ShowsConnectedMcpServers()
    {
        var settings = new AppSettings
        {
            McpServers = [new McpServerSettings { Name = "demo-server", Enabled = true }]
        };
        var coordinator = ComposerTestFactory.CreateCoordinator(
            settings: settings,
            mcpRegistry: new ComposerTestFactory.ConnectedMcpRegistry("demo-server", "browser_navigate"));

        var items = new ObservableCollection<AtCompletionItemViewModel>();
        var isOpen = false;

        coordinator.UpdateAtCompletion(
            "/",
            caretIndex: 1,
            activeWorkspace: null,
            ignorePatterns: [],
            items,
            open => isOpen = open,
            _ => { },
            -1);

        Assert.True(isOpen);
        Assert.Contains(items, item => item.Type == "MCP" && item.PrimaryText == "demo-server");
        Assert.DoesNotContain(items, item => item.PrimaryText == "browser_navigate");
        Assert.Contains(items, item => item.InsertText == "//mcp:demo-server");
    }

    [Fact]
    public void EnsureFileIndexBuilt_NoSkills_DoesNotRefreshAgainAfterInitialization()
    {
        var service = ComposerTestFactory.CreateCompletionService();
        var catalog = new ComposerTestFactory.StubSkillCatalog([]);
        var settings = new AppSettings();
        var updateCount = 0;
        var reentered = false;
        service.SourcesUpdated += () =>
        {
            updateCount++;
            if (!reentered)
            {
                reentered = true;
                service.EnsureFileIndexBuilt(catalog, settings, activeWorkspace: null, ignorePatterns: []);
            }
        };

        service.EnsureFileIndexBuilt(catalog, settings, activeWorkspace: null, ignorePatterns: []);
        service.EnsureFileIndexBuilt(catalog, settings, activeWorkspace: null, ignorePatterns: []);

        Assert.Equal(1, updateCount);
    }

    [Fact]
    public async Task EnsureFileIndexBuilt_EmptyWorkspace_DoesNotRefreshAfterEmptyIndexCompletes()
    {
        var workspace = Path.Combine(Path.GetTempPath(), "composer-empty-workspace-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        try
        {
            var service = ComposerTestFactory.CreateCompletionService();
            var catalog = new ComposerTestFactory.StubSkillCatalog([CreateSkill("test-skill")]);
            var indexCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var updateCount = 0;
            service.SourcesUpdated += () =>
            {
                if (Interlocked.Increment(ref updateCount) == 2)
                {
                    indexCompleted.TrySetResult();
                }
            };

            service.EnsureFileIndexBuilt(catalog, new AppSettings(), workspace, ignorePatterns: []);
            await indexCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var countAfterIndexCompleted = Volatile.Read(ref updateCount);

            service.EnsureFileIndexBuilt(catalog, new AppSettings(), workspace, ignorePatterns: []);

            Assert.Equal(countAfterIndexCompleted, Volatile.Read(ref updateCount));
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    private static AgentSkill CreateSkill(string name) =>
        new(
            new Dictionary<string, object>
            {
                ["name"] = name,
                ["description"] = $"{name} description"
            },
            $"# {name}");
}
