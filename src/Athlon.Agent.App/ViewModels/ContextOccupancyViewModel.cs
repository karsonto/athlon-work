using System.Windows.Media;
using Athlon.Agent.App.Resources;
using Athlon.Agent.Core.Compaction;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Athlon.Agent.App.ViewModels;

public sealed partial class ContextOccupancyViewModel : ObservableObject
{
    private const double RingCircumference = 40.84;
    private const double TrackWidth = 220;

    [ObservableProperty]
    private bool isVisible;

    [ObservableProperty]
    private bool isFlyoutOpen;

    [ObservableProperty]
    private int percentUsed;

    [ObservableProperty]
    private string percentLabel = string.Empty;

    [ObservableProperty]
    private string usedCapacityLabel = string.Empty;

    [ObservableProperty]
    private DoubleCollection ringDashArray = FrozenDash(0);

    [ObservableProperty]
    private ContextPressureLevel pressure = ContextPressureLevel.Normal;

    [ObservableProperty]
    private string historyLabel = string.Empty;

    [ObservableProperty]
    private string systemLabel = string.Empty;

    [ObservableProperty]
    private string toolsLabel = string.Empty;

    [ObservableProperty]
    private string marginLabel = string.Empty;

    [ObservableProperty]
    private double historyShare;

    [ObservableProperty]
    private double systemShare;

    [ObservableProperty]
    private double toolsShare;

    [ObservableProperty]
    private double marginShare;

    [ObservableProperty]
    private double historyBarWidth;

    [ObservableProperty]
    private double systemBarWidth;

    [ObservableProperty]
    private double toolsBarWidth;

    [ObservableProperty]
    private double marginBarWidth;

    public void Apply(ContextBudgetSnapshot budget, ContextPressureLevel pressure)
    {
        if (!budget.HasOccupancy)
        {
            IsVisible = false;
            return;
        }

        IsVisible = true;
        Pressure = pressure;
        var percent = (int)Math.Clamp(Math.Round(budget.TotalUtilization * 100), 0, 999);
        PercentUsed = percent;
        PercentLabel = Strings.Format("Chat_ContextMeterPercent", percent);
        UsedCapacityLabel = Strings.Format(
            "Chat_ContextMeterCapacity",
            TokenCountDisplay.FormatCompact(budget.EstimatedTotalPrompt),
            TokenCountDisplay.FormatCompact(budget.UsablePromptWindow));
        var dash = Math.Clamp(budget.TotalUtilization, 0, 1) * RingCircumference;
        RingDashArray = FrozenDash(dash);

        var total = Math.Max(1, budget.EstimatedTotalPrompt);
        HistoryShare = (double)budget.EstimatedHistory / total;
        SystemShare = (double)budget.SystemTokens / total;
        ToolsShare = (double)budget.ToolsTokens / total;
        MarginShare = (double)budget.MarginTokens / total;
        HistoryLabel = Strings.Format("Chat_ContextMeterMessages", TokenCountDisplay.FormatCompact(budget.EstimatedHistory));
        SystemLabel = Strings.Format("Chat_ContextMeterSystem", TokenCountDisplay.FormatCompact(budget.SystemTokens));
        ToolsLabel = Strings.Format("Chat_ContextMeterTools", TokenCountDisplay.FormatCompact(budget.ToolsTokens));
        MarginLabel = Strings.Format("Chat_ContextMeterMargin", TokenCountDisplay.FormatCompact(budget.MarginTokens));
        HistoryBarWidth = Math.Max(0, HistoryShare * TrackWidth);
        SystemBarWidth = Math.Max(0, SystemShare * TrackWidth);
        ToolsBarWidth = Math.Max(0, ToolsShare * TrackWidth);
        MarginBarWidth = Math.Max(0, MarginShare * TrackWidth);
    }

    public void ApplyOverflow()
    {
        Pressure = ContextPressureLevel.Overflow;
        if (IsVisible)
        {
            PercentUsed = Math.Max(PercentUsed, 100);
            PercentLabel = Strings.Format("Chat_ContextMeterPercent", PercentUsed);
            RingDashArray = FrozenDash(RingCircumference);
        }
    }

    private static DoubleCollection FrozenDash(double dash)
    {
        var collection = new DoubleCollection { dash, RingCircumference };
        collection.Freeze();
        return collection;
    }
}
