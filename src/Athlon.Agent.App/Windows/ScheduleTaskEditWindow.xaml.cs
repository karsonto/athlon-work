using System.IO;
using System.Windows;
using System.Windows.Controls;
using Athlon.Agent.App.Services;
using Athlon.Agent.Core;

namespace Athlon.Agent.App.Windows;

public partial class ScheduleTaskEditWindow : Window
{
    private readonly ScheduledTask _task;

    public ScheduleTaskEditWindow(ScheduledTask task, bool isNew = false)
    {
        InitializeComponent();
        _task = task;
        var dialogTitle = isNew ? "新建定时任务" : "编辑定时任务";
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
            MessageBox.Show("请输入任务名称。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            TitleBox.Focus();
            return;
        }

        if (string.IsNullOrWhiteSpace(WorkspaceBox.Text))
        {
            MessageBox.Show("请输入工作目录。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            WorkspaceBox.Focus();
            return;
        }

        var workspace = WorkspaceBox.Text.Trim();
        if (!Directory.Exists(workspace))
        {
            var confirm = MessageBox.Show(
                $"目录不存在：{workspace}\n仍要保存吗？",
                "提示",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.Yes)
            {
                WorkspaceBox.Focus();
                return;
            }
        }

        var kind = (KindCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "daily";

        if (kind == "daily" && !TimeOnly.TryParse(TimeOfDayBox.Text, out _))
        {
            MessageBox.Show("请输入有效的时间格式，例如 09:00。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            TimeOfDayBox.Focus();
            return;
        }

        if (kind == "interval" && (!int.TryParse(IntervalBox.Text, out var minutes) || minutes <= 0))
        {
            MessageBox.Show("请输入有效的间隔分钟数（正整数）。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            IntervalBox.Focus();
            return;
        }

        if (kind == "at" && !DateTime.TryParse(AtTimeBox.Text, out _))
        {
            MessageBox.Show("请输入有效的日期时间格式，例如 2025-12-31 18:00。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
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
