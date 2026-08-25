using System.IO;
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
        AtTimeBox.Text = task.AtTime;

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

        if (kind == "at" && !DateTime.TryParse(AtTimeBox.Text, out _))
        {
            _notifier.Warning("Common_Prompt", "Schedule_InvalidDateTime");
            AtTimeBox.Focus();
            return;
        }

        _task.Title = TitleBox.Text.Trim();
        _task.Prompt = PromptBox.Text;
        _task.Kind = kind;
        _task.TimeOfDay = TimeOfDayBox.Text.Trim();
        _task.EveryMinutes = int.TryParse(IntervalBox.Text, out var m) ? m : 60;
        _task.AtTime = AtTimeBox.Text.Trim();
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
