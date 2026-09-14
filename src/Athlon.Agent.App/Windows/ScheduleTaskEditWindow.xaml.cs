using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Athlon.Agent.App.Localization;
using Athlon.Agent.App.Services;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Knowledge;
using Athlon.Agent.Infrastructure.Prompt;
using Athlon.Agent.Skills;

namespace Athlon.Agent.App.Windows;

public partial class ScheduleTaskEditWindow : Window
{
    private readonly ScheduledTask _task;
    private readonly AppSettings _settings;
    private readonly IUserNotifier _notifier;
    private readonly ILocalizationService _loc;
    private readonly List<SelectableRow> _skillRows = [];
    private readonly List<SelectableRow> _mcpRows = [];
    private readonly List<SelectableRow> _knowledgeRows = [];
    private bool _atTimeInitialized;

    public ScheduleTaskEditWindow(
        ScheduledTask task,
        AppSettings settings,
        IAgentSkillCatalog skillCatalog,
        IKnowledgeStore knowledgeStore,
        IUserNotifier notifier,
        ILocalizationService localization,
        bool isNew = false)
    {
        InitializeComponent();
        _task = task;
        _settings = settings;
        _notifier = notifier;
        _loc = localization;
        var dialogTitle = isNew ? _loc["Schedule_NewTitle"] : _loc["Schedule_EditTitle"];
        Title = dialogTitle;
        HeaderText.Text = dialogTitle;

        TitleBox.Text = task.Title;
        PromptBox.Text = task.Prompt;
        WorkspaceBox.Text = task.WorkspaceRoot;
        ModelBox.Text = string.IsNullOrWhiteSpace(task.Model) || string.Equals(task.Model, "auto", StringComparison.OrdinalIgnoreCase)
            ? "auto"
            : task.Model;

        SelectComboByTag(KindCombo, task.Kind, fallbackIndex: 0);
        SelectComboByTag(ModeCombo, string.IsNullOrWhiteSpace(task.Mode) ? "agent" : task.Mode, fallbackIndex: 0);
        ComputerUseCheck.IsChecked = task.ComputerUse;

        TimeOfDayBox.Text = task.TimeOfDay;
        IntervalBox.Text = task.EveryMinutes.ToString();
        InitializeAtTimeInputs(task.AtTime);

        KindCombo.SelectionChanged += (_, _) => UpdatePanels();
        UpdatePanels();

        PopulateSkills(skillCatalog, task);
        PopulateMcp(task);
        _ = PopulateKnowledgeAsync(knowledgeStore, task);
    }

    private void PopulateSkills(IAgentSkillCatalog skillCatalog, ScheduledTask task)
    {
        var selected = new HashSet<string>(
            task.SkillNames ?? [],
            StringComparer.OrdinalIgnoreCase);
        var restrict = selected.Count > 0;

        foreach (var skill in SkillFilter.GetEnabledSkills(skillCatalog, _settings))
        {
            var row = new SelectableRow(skill.Name, !restrict || selected.Contains(skill.Name));
            // Empty allow-list means inherit all — show all unchecked visually? Plan: empty = inherit.
            // UI: when inherit, leave all unchecked; when restricted, check selected.
            row.IsChecked = restrict && selected.Contains(skill.Name);
            _skillRows.Add(row);
        }

        SkillsList.ItemsSource = _skillRows;
    }

    private void PopulateMcp(ScheduledTask task)
    {
        var selected = new HashSet<string>(
            task.McpServerNames ?? [],
            StringComparer.OrdinalIgnoreCase);
        var restrict = selected.Count > 0;

        foreach (var server in _settings.McpServers.Where(s => s.Enabled && !string.IsNullOrWhiteSpace(s.Name)))
        {
            var name = server.Name.Trim();
            _mcpRows.Add(new SelectableRow(name, restrict && selected.Contains(name)));
        }

        McpList.ItemsSource = _mcpRows;
    }

    private async Task PopulateKnowledgeAsync(IKnowledgeStore knowledgeStore, ScheduledTask task)
    {
        var selected = new HashSet<string>(
            task.KnowledgeModuleIds ?? [],
            StringComparer.OrdinalIgnoreCase);

        try
        {
            var modules = await knowledgeStore.ListModulesAsync().ConfigureAwait(true);
            foreach (var summary in modules)
            {
                var module = summary.Module;
                _knowledgeRows.Add(new SelectableRow(
                    module.Id,
                    selected.Contains(module.Id),
                    display: string.IsNullOrWhiteSpace(module.Name) ? module.Id : $"{module.Name} ({module.Id})"));
            }
        }
        catch
        {
            // Knowledge store may be unavailable; leave empty list.
        }

        KnowledgeList.ItemsSource = _knowledgeRows;
    }

    private static void SelectComboByTag(ComboBox combo, string? tag, int fallbackIndex)
    {
        foreach (var item in combo.Items)
        {
            if (item is ComboBoxItem cbi && string.Equals(cbi.Tag?.ToString(), tag, StringComparison.OrdinalIgnoreCase))
            {
                combo.SelectedItem = item;
                return;
            }
        }

        combo.SelectedIndex = fallbackIndex;
    }

    private void UpdatePanels()
    {
        var kind = (KindCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "daily";
        DailyPanel.Visibility = kind == "daily" ? Visibility.Visible : Visibility.Collapsed;
        IntervalPanel.Visibility = kind == "interval" ? Visibility.Visible : Visibility.Collapsed;
        AtPanel.Visibility = kind == "at" ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Fills the one-time hour/minute pickers and parses the stored value into the date picker.
    /// A missing or unparseable value falls back to "one hour from now", so a fresh task always has
    /// a concrete, already-valid selection.
    /// </summary>
    private void InitializeAtTimeInputs(string? atTime)
    {
        AtHourCombo.ItemsSource = Enumerable.Range(0, 24).Select(h => h.ToString("00")).ToList();
        AtMinuteCombo.ItemsSource = Enumerable.Range(0, 60).Select(m => m.ToString("00")).ToList();

        if (!DateTime.TryParse(atTime, out var parsed))
        {
            parsed = DateTime.Now.AddHours(1);
        }

        AtDatePicker.SelectedDate = parsed.Date;
        SelectComboItem(AtHourCombo, parsed.Hour.ToString("00"));
        SelectComboItem(AtMinuteCombo, parsed.Minute.ToString("00"));
    }

    private static void SelectComboItem(ComboBox combo, string tag)
    {
        foreach (var item in combo.Items)
        {
            if (string.Equals(item?.ToString(), tag, StringComparison.Ordinal))
            {
                combo.SelectedItem = item;
                return;
            }
        }

        if (combo.Items.Count > 0)
        {
            combo.SelectedIndex = 0;
        }
    }

    /// <summary>
    /// Picking a day only needs to fill the time-of-day pickers the first time; after that the user
    /// owns them. Existing tasks keep their stored time instead of snapping to now.
    /// </summary>
    private void AtDatePicker_OnSelectedDateChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (AtDatePicker.SelectedDate is not { } date || _atTimeInitialized)
        {
            return;
        }

        _atTimeInitialized = true;
        if (AtHourCombo.SelectedItem is null)
        {
            SelectComboItem(AtHourCombo, date.TimeOfDay.Hours.ToString("00"));
            SelectComboItem(AtMinuteCombo, date.TimeOfDay.Minutes.ToString("00"));
        }
    }

    /// <summary>Reads the one-time pickers as a local <see cref="DateTime"/>.</summary>
    private DateTime? ReadAtTime()
    {
        if (AtDatePicker.SelectedDate is not { } date)
        {
            return null;
        }

        if (!int.TryParse(AtHourCombo.SelectedItem?.ToString(), out var hour)
            || !int.TryParse(AtMinuteCombo.SelectedItem?.ToString(), out var minute))
        {
            return null;
        }

        return date.Date.AddHours(hour).AddMinutes(minute);
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(TitleBox.Text))
        {
            _notifier.Warning("Common_Prompt", "Schedule_TitleRequired");
            TitleBox.Focus();
            return;
        }

        var workspace = WorkspaceBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(workspace))
        {
            _notifier.Warning("Common_Prompt", "Schedule_WorkspaceRequiredPrompt");
            WorkspaceBox.Focus();
            return;
        }

        if (!string.IsNullOrWhiteSpace(workspace) && !Directory.Exists(workspace))
        {
            if (!_notifier.ConfirmYesNo("Common_Prompt", "Schedule_WorkspaceMissing", workspace))
            {
                WorkspaceBox.Focus();
                return;
            }
        }

        var kind = (KindCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "daily";

        if (kind == "daily" && !TimeOnly.TryParse(TimeOfDayBox.Text, out _))
        {
            _notifier.Warning("Common_Prompt", "Schedule_InvalidTime");
            TimeOfDayBox.Focus();
            return;
        }

        if (kind == "interval" && (!int.TryParse(IntervalBox.Text, out var minutes) || minutes <= 0))
        {
            _notifier.Warning("Common_Prompt", "Schedule_InvalidInterval");
            IntervalBox.Focus();
            return;
        }

        if (kind == "at" && ReadAtTime() is not { } atTime)
        {
            _notifier.Warning("Common_Prompt", "Schedule_InvalidDateTime");
            AtDatePicker.Focus();
            return;
        }

        _task.Title = TitleBox.Text.Trim();
        _task.Prompt = PromptBox.Text;
        _task.Kind = kind;
        _task.TimeOfDay = TimeOfDayBox.Text.Trim();
        _task.EveryMinutes = int.TryParse(IntervalBox.Text, out var m) ? m : 60;
        // Store the pickers as a local, round-trippable timestamp; ScheduleTiming converts it to
        // UTC itself when it decides whether the task is due.
        _task.AtTime = kind == "at" && ReadAtTime() is { } atValue
            ? atValue.ToString("yyyy-MM-dd HH:mm")
            : _task.AtTime;
        _task.WorkspaceRoot = workspace;
        _task.Mode = (ModeCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "agent";
        _task.ComputerUse = ComputerUseCheck.IsChecked == true;
        var model = ModelBox.Text.Trim();
        _task.Model = string.IsNullOrWhiteSpace(model) ? "auto" : model;
        _task.SkillNames = _skillRows.Where(r => r.IsChecked).Select(r => r.Id).ToList();
        _task.McpServerNames = _mcpRows.Where(r => r.IsChecked).Select(r => r.Id).ToList();
        _task.KnowledgeModuleIds = _knowledgeRows.Where(r => r.IsChecked).Select(r => r.Id).ToList();
        _task.UpdatedAt = DateTime.UtcNow.ToString("O");
        ScheduleTiming.EnsureNextRunAt(_task);

        DialogResult = true;
        Close();
    }

    public sealed class SelectableRow
    {
        public SelectableRow(string id, bool isChecked, string? display = null)
        {
            Id = id;
            Display = display ?? id;
            IsChecked = isChecked;
        }

        public string Id { get; }
        public string Display { get; }
        public bool IsChecked { get; set; }
    }
}
