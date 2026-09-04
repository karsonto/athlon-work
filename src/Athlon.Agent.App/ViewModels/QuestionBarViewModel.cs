using System.Collections.ObjectModel;
using System.Windows;
using Athlon.Agent.App.Localization;
using Athlon.Agent.Core.Plan;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Athlon.Agent.App.ViewModels;

/// <summary>One selectable option within a <see cref="QuestionItemViewModel"/>.</summary>
public sealed partial class QuestionOptionViewModel : ObservableObject
{
    private readonly Action<QuestionOptionViewModel>? _onToggled;

    public QuestionOptionViewModel(string id, string label, Action<QuestionOptionViewModel>? onToggled = null)
    {
        Id = id;
        Label = label;
        _onToggled = onToggled;
    }

    public string Id { get; }

    public string Label { get; }

    [ObservableProperty]
    private bool _isSelected;

    partial void OnIsSelectedChanged(bool value)
    {
        if (value)
        {
            _onToggled?.Invoke(this);
        }
    }
}

/// <summary>One question (prompt + options) rendered inside the QuestionBar.</summary>
public sealed partial class QuestionItemViewModel : ObservableObject
{
    public QuestionItemViewModel(
        string id,
        string prompt,
        bool allowMultiple,
        IEnumerable<UserQuestionOption> options)
    {
        Id = id;
        Prompt = prompt;
        AllowMultiple = allowMultiple;
        Options = new ObservableCollection<QuestionOptionViewModel>(
            options.Select(o => new QuestionOptionViewModel(
                o.Id,
                o.Label,
                allowMultiple ? null : UnselectOthers)));
    }

    public string Id { get; }

    public string Prompt { get; }

    public bool AllowMultiple { get; }

    public ObservableCollection<QuestionOptionViewModel> Options { get; }

    private void UnselectOthers(QuestionOptionViewModel selected)
    {
        foreach (var option in Options)
        {
            if (!ReferenceEquals(option, selected))
            {
                option.IsSelected = false;
            }
        }
    }
}

/// <summary>
/// Shows the pending <c>ask_user</c> question set above the composer. The model's
/// question lives only in process memory (<see cref="IUserQuestionState"/>); once the
/// user submits (or dismisses) the bar, the formatted answer is handed to the shell
/// which starts the next turn with it.
/// </summary>
public sealed partial class QuestionBarViewModel : ObservableObject
{
    private readonly IUserQuestionState _userQuestions;
    private readonly ILocalizationService _loc;

    private Func<string>? _getDisplayedSessionId;
    private Action<string?, ShellToastKind>? _showToast;
    private Action<string>? _onSubmitText;

    public QuestionBarViewModel(IUserQuestionState userQuestions, ILocalizationService localization)
    {
        _userQuestions = userQuestions;
        _loc = localization;
        _userQuestions.QuestionChanged += OnQuestionChanged;
    }

    public void Configure(
        Func<string> getDisplayedSessionId,
        Action<string?, ShellToastKind> showToast,
        Action<string> onSubmitText)
    {
        _getDisplayedSessionId = getDisplayedSessionId;
        _showToast = showToast;
        _onSubmitText = onSubmitText;
        RefreshFromActiveSession();
    }

    [ObservableProperty]
    private bool _isVisible;

    [ObservableProperty]
    private bool _showFreeText;

    [ObservableProperty]
    private string _freeText = string.Empty;

    public ObservableCollection<QuestionItemViewModel> Items { get; } = [];

    /// <summary>Re-reads the pending question for the currently displayed session.</summary>
    public void RefreshFromActiveSession()
    {
        var sessionId = _getDisplayedSessionId?.Invoke();
        var question = string.IsNullOrWhiteSpace(sessionId)
            ? null
            : _userQuestions.GetPending(sessionId);
        Apply(question);
    }

    [RelayCommand]
    private void Submit()
    {
        if (_getDisplayedSessionId?.Invoke() is not { } sessionId)
        {
            return;
        }

        var question = _userQuestions.GetPending(sessionId);
        if (question is null)
        {
            return;
        }

        var selections = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        var hasSelection = false;
        foreach (var item in Items)
        {
            var ids = item.Options.Where(o => o.IsSelected).Select(o => o.Id).ToList();
            if (ids.Count > 0)
            {
                selections[item.Id] = ids;
                hasSelection = true;
            }
        }

        var hasNote = !string.IsNullOrWhiteSpace(FreeText);
        if (!hasSelection && !hasNote)
        {
            _showToast?.Invoke(_loc["AskUser_EmptyAnswer"], ShellToastKind.Error);
            return;
        }

        var text = UserQuestion.FormatUserAnswer(question, selections, FreeText);
        _onSubmitText?.Invoke(text);
    }

    [RelayCommand]
    private void Cancel()
    {
        if (_getDisplayedSessionId?.Invoke() is not { } sessionId)
        {
            return;
        }

        _userQuestions.Clear(sessionId);
    }

    private void Apply(UserQuestion? question)
    {
        Items.Clear();
        if (question is null || question.Questions.Count == 0)
        {
            IsVisible = false;
            ShowFreeText = false;
            FreeText = string.Empty;
            return;
        }

        ShowFreeText = question.AllowFreeText;
        FreeText = string.Empty;
        foreach (var item in question.Questions)
        {
            Items.Add(new QuestionItemViewModel(item.Id, item.Prompt, item.AllowMultiple, item.Options));
        }

        IsVisible = true;
    }

    private void OnQuestionChanged(object? sender, UserQuestionChangedEventArgs e)
    {
        var sessionId = _getDisplayedSessionId?.Invoke();
        if (string.IsNullOrWhiteSpace(sessionId)
            || !string.Equals(e.SessionId, sessionId, StringComparison.Ordinal))
        {
            return;
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            dispatcher.InvokeAsync(RefreshFromActiveSession);
            return;
        }

        RefreshFromActiveSession();
    }
}
