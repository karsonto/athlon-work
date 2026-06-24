using System.Collections.ObjectModel;
using Athlon.Agent.App.Services;
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
        var coordinator = new ComposerCoordinator(
            new ComposerAtCompletionService(),
            new AgentSkillCatalog(new FileSystemSkillRepository(skillRoot)),
            new StubImageAttachmentStore(),
            new AppPathProvider());

        var items = new ObservableCollection<AtCompletionItemViewModel>
        {
            new("文件", "readme.md", "/docs/readme.md", "readme.md", "readme.md")
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
            out var newCaret);

        Assert.True(accepted);
        Assert.Contains("readme.md", composerText);
        Assert.True(newCaret > 0);
    }

    [Fact]
    public void CloseAtCompletion_ClearsItemsAndResetsSelection()
    {
        var skillRoot = Path.Combine(Path.GetTempPath(), "composer-coord-skills-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(skillRoot);
        var coordinator = new ComposerCoordinator(
            new ComposerAtCompletionService(),
            new AgentSkillCatalog(new FileSystemSkillRepository(skillRoot)),
            new StubImageAttachmentStore(),
            new AppPathProvider());

        var items = new ObservableCollection<AtCompletionItemViewModel>
        {
            new("文件", "a.txt", "a.txt", "a.txt", "a.txt")
        };
        var isOpen = true;
        var selected = 0;

        coordinator.CloseAtCompletion(items, open => isOpen = open, index => selected = index);

        Assert.False(isOpen);
        Assert.Equal(-1, selected);
        Assert.Empty(items);
    }

    private sealed class StubImageAttachmentStore : IImageAttachmentStore
    {
        public ImageAttachment SaveFromFile(string sessionId, string sourcePath) =>
            new(Path.GetFileName(sourcePath), "image/png", LocalPath: sourcePath);
    }
}
