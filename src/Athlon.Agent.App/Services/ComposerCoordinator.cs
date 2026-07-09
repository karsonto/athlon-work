using System.Collections.ObjectModel;
using System.IO;
using Athlon.Agent.App.ViewModels;
using Athlon.Agent.Core;
using Athlon.Agent.Infrastructure;
using Athlon.Agent.Skills;

namespace Athlon.Agent.App.Services;

public sealed class ComposerCoordinator
{
    private readonly ComposerAtCompletionService _atCompletion;
    private readonly IAgentSkillCatalog _skillCatalog;
    private readonly AppSettings _settings;
    private readonly IImageAttachmentStore _imageAttachmentStore;
    private readonly IAppPathProvider _paths;

    public ComposerCoordinator(
        ComposerAtCompletionService atCompletion,
        IAgentSkillCatalog skillCatalog,
        AppSettings settings,
        IImageAttachmentStore imageAttachmentStore,
        IAppPathProvider paths)
    {
        _atCompletion = atCompletion;
        _skillCatalog = skillCatalog;
        _settings = settings;
        _imageAttachmentStore = imageAttachmentStore;
        _paths = paths;
        _atCompletion.SourcesUpdated += OnAtCompletionSourcesUpdated;
    }

    public event Action? AtCompletionSourcesUpdated;

    private void OnAtCompletionSourcesUpdated() => AtCompletionSourcesUpdated?.Invoke();

    public void RefreshSources(
        string? activeWorkspace,
        IReadOnlyCollection<string> ignorePatterns,
        bool reloadSkills = false) =>
        _atCompletion.RefreshSources(_skillCatalog, _settings, activeWorkspace, ignorePatterns, reloadSkills);

    public void EnsureFileIndexBuilt(string? activeWorkspace, IReadOnlyCollection<string> ignorePatterns) =>
        _atCompletion.EnsureFileIndexBuilt(_skillCatalog, _settings, activeWorkspace, ignorePatterns);

    public void UpdateAtCompletion(
        string composerText,
        int caretIndex,
        string? activeWorkspace,
        IReadOnlyCollection<string> ignorePatterns,
        ObservableCollection<AtCompletionItemViewModel> items,
        Action<bool> setIsOpen,
        Action<int> setSelectedIndex,
        int selectedIndex)
    {
        if (!ComposerAtCompletionService.TryGetQuery(composerText, caretIndex, out var query))
        {
            CloseAtCompletion(items, setIsOpen, setSelectedIndex);
            return;
        }

        EnsureFileIndexBuilt(activeWorkspace, ignorePatterns);
        var sorted = _atCompletion.FilterMatches(query);
        ReplaceCompletionItems(items, sorted);

        if (items.Count == 0)
        {
            CloseAtCompletion(items, setIsOpen, setSelectedIndex);
            return;
        }

        setIsOpen(true);
        if (selectedIndex < 0 || selectedIndex >= items.Count)
        {
            setSelectedIndex(0);
        }
    }

    public void MoveSelection(
        int delta,
        bool isOpen,
        int itemCount,
        int selectedIndex,
        Action<int> setSelectedIndex)
    {
        if (!isOpen || itemCount == 0)
        {
            return;
        }

        var next = selectedIndex + delta;
        if (next < 0)
        {
            next = itemCount - 1;
        }
        else if (next >= itemCount)
        {
            next = 0;
        }

        setSelectedIndex(next);
    }

    public bool TryAcceptAtCompletion(
        string composerText,
        int caretIndex,
        bool isOpen,
        int selectedIndex,
        IReadOnlyList<AtCompletionItemViewModel> items,
        Action<string> setComposerText,
        Action closeCompletion,
        out int newCaretIndex)
    {
        newCaretIndex = caretIndex;
        if (!isOpen
            || selectedIndex < 0
            || selectedIndex >= items.Count
            || !ComposerCompletionQuery.TryGetAtQuerySpan(composerText, caretIndex, out var atStart, out var atEndExclusive))
        {
            return false;
        }

        var replacement = ComposerAtCompletionService.FormatReplacement(items[selectedIndex]);
        setComposerText(composerText[..atStart] + replacement + composerText[atEndExclusive..]);
        newCaretIndex = atStart + replacement.Length;
        closeCompletion();
        return true;
    }

    public void CloseAtCompletion(
        ObservableCollection<AtCompletionItemViewModel> items,
        Action<bool> setIsOpen,
        Action<int> setSelectedIndex)
    {
        setIsOpen(false);
        setSelectedIndex(-1);
        items.Clear();
    }

    public void AddPendingImages(
        IEnumerable<ImageAttachment> images,
        ObservableCollection<PendingImageAttachmentViewModel> pending)
    {
        foreach (var image in images)
        {
            if (pending.Any(existing => ImageAttachmentsMatch(existing.Attachment, image)))
            {
                continue;
            }

            pending.Add(new PendingImageAttachmentViewModel(image));
        }
    }

    public ImageAttachment[] PersistPendingImages(
        string sessionId,
        IEnumerable<PendingImageAttachmentViewModel> pending)
    {
        var persisted = new List<ImageAttachment>();
        foreach (var item in pending)
        {
            persisted.Add(PersistImageAttachment(sessionId, item.Attachment));
        }

        return persisted.ToArray();
    }

    private ImageAttachment PersistImageAttachment(string sessionId, ImageAttachment attachment)
    {
        if (!string.IsNullOrWhiteSpace(attachment.DataUrl))
        {
            return attachment;
        }

        if (string.IsNullOrWhiteSpace(attachment.LocalPath))
        {
            return attachment;
        }

        var sessionAttachmentsRoot = Path.Combine(_paths.SessionsPath, sessionId, "attachments");
        if (attachment.LocalPath.StartsWith(sessionAttachmentsRoot, StringComparison.OrdinalIgnoreCase))
        {
            return attachment;
        }

        return _imageAttachmentStore.SaveFromFile(sessionId, attachment.LocalPath);
    }

    private static void ReplaceCompletionItems(
        ObservableCollection<AtCompletionItemViewModel> items,
        IReadOnlyList<AtCompletionItemViewModel> next)
    {
        items.Clear();
        foreach (var item in next)
        {
            items.Add(item);
        }
    }

    private static bool ImageAttachmentsMatch(ImageAttachment left, ImageAttachment right) =>
        (!string.IsNullOrWhiteSpace(left.LocalPath)
            && string.Equals(left.LocalPath, right.LocalPath, StringComparison.OrdinalIgnoreCase))
        || (!string.IsNullOrWhiteSpace(left.DataUrl)
            && string.Equals(left.DataUrl, right.DataUrl, StringComparison.Ordinal));
}
