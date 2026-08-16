using System.IO;
using Athlon.Agent.Core.Harness;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Athlon.Agent.App.ViewModels;

public enum EditorViewMode
{
    Code,
    Preview
}

public sealed partial class EditorDocumentViewModel : ObservableObject
{
    public const string SessionPlanPathPrefix = "athlon-session-plan:";

    private string _content = string.Empty;
    private string _savedContent = string.Empty;
    private string _displayName;
    private string? _relativePath;
    private string _tabTitle;

    public EditorDocumentViewModel(string filePath, string content, string? relativePath, bool isReadOnly = false)
    {
        FilePath = filePath;
        _relativePath = relativePath;
        _displayName = Path.GetFileName(filePath);
        _tabTitle = _displayName;
        _content = content;
        _savedContent = content;
        _isReadOnly = isReadOnly;
        _viewMode = CanPreview ? EditorViewMode.Preview : EditorViewMode.Code;
    }

    public static EditorDocumentViewModel CreateSessionPlan(string sessionId, SessionPlan plan)
    {
        var fileName = BuildPlanFileName(plan.Title);
        var markdown = Services.PlanDocumentHtmlBuilder.ComposeMarkdown(plan.Title, plan.Overview, plan.Body);
        var document = new EditorDocumentViewModel(
            BuildSessionPlanPath(sessionId),
            markdown,
            $"Plans/{fileName}",
            isReadOnly: true);
        document.IsSessionPlan = true;
        document.SessionId = sessionId;
        document.PlanTitle = plan.Title ?? "";
        document.PlanOverview = plan.Overview ?? "";
        document.PlanBody = plan.Body ?? "";
        document.PlanStatus = SessionPlanStatuses.Normalize(plan.Status);
        document.PlanUpdatedAt = plan.UpdatedAt ?? "";
        document.ApplySessionPlanDisplay(fileName);
        document.ViewMode = EditorViewMode.Preview;
        return document;
    }

    public string FilePath { get; }
    public string? RelativePath => _relativePath;
    public string DisplayName => _displayName;
    public string TabTitle => _tabTitle;

    public string PathLabel =>
        string.IsNullOrWhiteSpace(RelativePath)
            ? DisplayName
            : RelativePath.Replace("/", " › ", StringComparison.Ordinal)
                .Replace("\\", " › ", StringComparison.Ordinal);

    public string Content
    {
        get => _content;
        set
        {
            if (SetProperty(ref _content, value))
            {
                if (!IsSessionPlan)
                {
                    IsDirty = !string.Equals(_content, _savedContent, StringComparison.Ordinal);
                }

                UpdateTabTitle();
            }
        }
    }

    [ObservableProperty]
    private bool _isDirty;

    [ObservableProperty]
    private bool _isReadOnly;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowPreview))]
    private EditorViewMode _viewMode;

    public bool IsSessionPlan { get; private set; }

    public string? SessionId { get; private set; }

    public string PlanTitle { get; private set; } = "";

    public string PlanOverview { get; private set; } = "";

    public string PlanBody { get; private set; } = "";

    public string PlanStatus { get; private set; } = SessionPlanStatuses.Draft;

    public string PlanUpdatedAt { get; private set; } = "";

    public bool CanBuild =>
        IsSessionPlan
        && string.Equals(PlanStatus, SessionPlanStatuses.AwaitingConfirmation, StringComparison.OrdinalIgnoreCase);

    public bool UsePlanHtmlPreview => IsSessionPlan;

    public bool IsMarkdownFile
    {
        get
        {
            if (IsSessionPlan)
            {
                return true;
            }

            var extension = Path.GetExtension(FilePath);
            return extension.Equals(".md", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".markdown", StringComparison.OrdinalIgnoreCase);
        }
    }

    public bool IsHtmlFile
    {
        get
        {
            var extension = Path.GetExtension(FilePath);
            return extension.Equals(".html", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".htm", StringComparison.OrdinalIgnoreCase);
        }
    }

    public bool CanPreview => IsMarkdownFile || IsHtmlFile || IsSessionPlan;

    public bool ShowPreview => CanPreview && ViewMode == EditorViewMode.Preview;

    public static string BuildSessionPlanPath(string sessionId) =>
        $"{SessionPlanPathPrefix}{sessionId}";

    public static bool IsSessionPlanPath(string? path) =>
        !string.IsNullOrWhiteSpace(path)
        && path.StartsWith(SessionPlanPathPrefix, StringComparison.OrdinalIgnoreCase);

    public void ApplySessionPlan(SessionPlan plan)
    {
        if (!IsSessionPlan)
        {
            return;
        }

        PlanTitle = plan.Title ?? "";
        PlanOverview = plan.Overview ?? "";
        PlanBody = plan.Body ?? "";
        PlanStatus = SessionPlanStatuses.Normalize(plan.Status);
        PlanUpdatedAt = plan.UpdatedAt ?? "";

        var fileName = BuildPlanFileName(PlanTitle);
        ApplySessionPlanDisplay(fileName);

        var markdown = Services.PlanDocumentHtmlBuilder.ComposeMarkdown(PlanTitle, PlanOverview, PlanBody);
        _savedContent = markdown;
        if (!SetProperty(ref _content, markdown, nameof(Content)))
        {
            OnPropertyChanged(nameof(Content));
        }

        IsDirty = false;
        OnPropertyChanged(nameof(CanBuild));
        OnPropertyChanged(nameof(PlanTitle));
        OnPropertyChanged(nameof(PlanOverview));
        OnPropertyChanged(nameof(PlanBody));
        OnPropertyChanged(nameof(PlanStatus));
        OnPropertyChanged(nameof(PathLabel));
    }

    public void MarkSaved(string content)
    {
        _savedContent = content;
        if (!SetProperty(ref _content, content, nameof(Content)))
        {
            OnPropertyChanged(nameof(Content));
        }

        IsDirty = false;
        UpdateTabTitle();
    }

    public void ReloadFromDisk(string content) => MarkSaved(content);

    private void ApplySessionPlanDisplay(string fileName)
    {
        _displayName = fileName;
        _relativePath = $"Plans/{fileName}";
        UpdateTabTitle();
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(RelativePath));
        OnPropertyChanged(nameof(PathLabel));
    }

    private void UpdateTabTitle()
    {
        var next = IsDirty ? $"{_displayName} ●" : _displayName;
        if (string.Equals(_tabTitle, next, StringComparison.Ordinal))
        {
            return;
        }

        _tabTitle = next;
        OnPropertyChanged(nameof(TabTitle));
    }

    private static string BuildPlanFileName(string? title)
    {
        var raw = string.IsNullOrWhiteSpace(title) ? "plan" : title.Trim();
        Span<char> buffer = stackalloc char[Math.Min(raw.Length, 80)];
        var written = 0;
        foreach (var ch in raw)
        {
            if (written >= buffer.Length)
            {
                break;
            }

            if (char.IsLetterOrDigit(ch) || ch is '-' or '_')
            {
                buffer[written++] = ch;
            }
            else if (char.IsWhiteSpace(ch) || ch is '.' or '/')
            {
                if (written > 0 && buffer[written - 1] != '_')
                {
                    buffer[written++] = '_';
                }
            }
        }

        var slug = written == 0 ? "plan" : new string(buffer[..written]).Trim('_');
        if (!slug.EndsWith(".plan.md", StringComparison.OrdinalIgnoreCase))
        {
            slug = $"{slug}.plan.md";
        }

        return slug;
    }
}
