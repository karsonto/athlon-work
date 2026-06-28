using System.IO;
using System.Windows;
using System.Windows.Controls;
using Athlon.Agent.App.Localization;
using Athlon.Agent.App.Services;
using Athlon.Agent.Core;

namespace Athlon.Agent.App.Windows;

public partial class ScheduleTaskEditWindow : Window
{
    private readonly ScheduledTask _task;
    private readonly IUserNotifier _notifier;
    private readonly ILocalizationService _loc;

    public ScheduleTaskEditWindow(
        ScheduledTask task,
        IUserNotifier notifier,
        ILocalizationService localization,
        bool isNew = false)
    {
        InitializeComponent();
        _task = task;
        _notifier = notifier;
        _loc = localization;
        var dialogTitle = isNew ? _loc["Schedule_NewTitle"] : _loc["Schedule_EditTitle"];
        Title = dialogTitle;
        HeaderText.Text = dialogTitle;

        TitleBox.Text = task.Title;
        PromptBox.Text = task.Prompt;
        WorkspaceBox.Text = task.WorkspaceRoot;

        foreach (var item in KindCombo.Items)
        {
            if (item is ComboBoxItem cbi && cbi.Tag?.ToString() == task.Kind)
            {
                KindCombo.SelectedItem = item;
                break;
            }
        }

        if (KindCombo.SelectedItem is null)
        {
            KindCombo.SelectedIndex = 0;
        }

        TimeOfDayBox.Text = task.TimeOfDay;
        IntervalBox.Text = task.EveryMinutes.ToString();
        AtTimeBox.Text = task.AtTime;

        KindCombo.SelectionChanged += (_, _) => UpdatePanels();
        UpdatePanels();
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

        if (string.IsNullOrWhiteSpace(WorkspaceBox.Text))
        {
            _notifier.Warning("Common_Prompt", "Schedule_WorkspaceRequiredPrompt");
            WorkspaceBox.Focus();
            return;
        }

        var workspace = WorkspaceBox.Text.Trim();
        if (!Directory.Exists(workspace))
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
        _task.Mode = "agent";
        _task.Model = "auto";
        _task.UpdatedAt = DateTime.UtcNow.ToString("O");
        ScheduleTiming.EnsureNextRunAt(_task);

        DialogResult = true;
        Close();
    }
}
