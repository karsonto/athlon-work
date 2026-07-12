using System.Collections.ObjectModel;
using System.IO;
using Athlon.Agent.App.Services.SlashCommands;
using Athlon.Agent.App.ViewModels;
using Athlon.Agent.Core;
using Athlon.Agent.Infrastructure;
using Athlon.Agent.Skills;

namespace Athlon.Agent.App.Services;

public sealed class ComposerCoordinator
{
    private readonly ComposerAtCompletionService _atCompletion;
    private readonly IComposerSlashCommandRegistry _slashRegistry;
    private readonly ComposerSlashCommandExecutor _slashExecutor;
    private readonly IAgentSkillCatalog _skillCatalog;
    private readonly AppSettings _settings;
    private readonly IImageAttachmentStore _imageAttachmentStore;
    private readonly IAppPathProvider _paths;

    public ComposerCoordinator(
        ComposerAtCompletionService atCompletion,
        IComposerSlashCommandRegistry slashRegistry,
        ComposerSlashCommandExecutor slashExecutor,
        IAgentSkillCatalog skillCatalog,
        AppSettings settings,
        IImageAttachmentStore imageAttachmentStore,
        IAppPathProvider paths)
    {
        _atCompletion = atCompletion;
        _slashRegistry = slashRegistry;
        _slashExecutor = slashExecutor;
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
        if (!ComposerAtCompletionService.TryGetQuery(
                composerText,
                caretIndex,
                _slashRegistry,
                out var trigger,
                out var query))
        {
            CloseAtCompletion(items, setIsOpen, setSelectedIndex);
            return;
        }

        if (trigger == ComposerCompletionTrigger.At)
        {
            EnsureFileIndexBuilt(activeWorkspace, ignorePatterns);
        }
        else
        {
            _atCompletion.EnsureSlashSourcesBuilt(_skillCatalog, _settings, activeWorkspace, ignorePatterns);
        }

        var sorted = _atCompletion.FilterMatches(trigger, query);
        items.Clear();
        foreach (var item in sorted)
        {
            items.Add(item);
        }

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
        ComposerSlashCommandContext? slashContext,
        out int newCaretIndex)
    {
        newCaretIndex = caretIndex;
        if (!isOpen
            || selectedIndex < 0
            || selectedIndex >= items.Count
            || !ComposerCompletionQuery.TryGetActiveQuery(
                composerText,
                caretIndex,
                _slashRegistry,
                out _,
                out var triggerStart,
                out var queryEndExclusive,
                out _))
        {
            return false;
        }

        var selected = items[selectedIndex];
        if (selected.Kind == ComposerCompletionItemKind.SlashCommand)
        {
            if (slashContext is null
                || string.IsNullOrWhiteSpace(selected.SlashCommandName)
                || !_slashRegistry.TryGetExact(selected.SlashCommandName, out var command)
                || command is null)
            {
                return false;
            }

            var result = _slashExecutor.ExecuteAsync(command, slashContext).AsTask().GetAwaiter().GetResult();
            if (result.StatusMessage is not null)
            {
                slashContext.SetStatus(result.StatusMessage);
            }

            if (result.Handled)
            {
                setComposerText(string.Empty);
                slashContext.NotifyCommandStatesChanged();
            }

            closeCompletion();
            newCaretIndex = 0;
            return result.Handled;
        }

        var replacement = ComposerAtCompletionService.FormatReplacement(selected);
        setComposerText(composerText[..triggerStart] + replacement + composerText[queryEndExclusive..]);
        newCaretIndex = triggerStart + replacement.Length;
        closeCompletion();
        return true;
    }

    public async Task<bool> TryExecuteSlashCommandAsync(
        string composerText,
        ComposerSlashCommandContext slashContext,
        Action<string> setComposerText,
        CancellationToken cancellationToken = default)
    {
        if (_slashExecutor.TryParseExactCommand(composerText, out var command) && command is not null)
        {
            var result = await _slashExecutor.ExecuteAsync(command, slashContext, cancellationToken).ConfigureAwait(true);
            if (result.StatusMessage is not null)
            {
                slashContext.SetStatus(result.StatusMessage);
            }

            if (result.Handled)
            {
                setComposerText(string.Empty);
                slashContext.NotifyCommandStatesChanged();
            }

            return result.Handled;
        }

        if (_slashExecutor.LooksLikeUnregisteredExactCommand(composerText))
        {
            slashContext.SetStatus($"Unknown slash command: {composerText.Trim()}");
            setComposerText(string.Empty);
            return true;
        }

        return false;
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

    private static bool ImageAttachmentsMatch(ImageAttachment left, ImageAttachment right) =>
        (!string.IsNullOrWhiteSpace(left.LocalPath)
            && string.Equals(left.LocalPath, right.LocalPath, StringComparison.OrdinalIgnoreCase))
        || (!string.IsNullOrWhiteSpace(left.DataUrl)
            && string.Equals(left.DataUrl, right.DataUrl, StringComparison.Ordinal));
}
