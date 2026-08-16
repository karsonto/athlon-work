using System.Windows.Media;
using Athlon.Agent.App.Resources;
using Athlon.Agent.Core.Compaction;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Athlon.Agent.App.ViewModels;

public sealed class ContextOccupancyCategoryRow
{
    public required string Id { get; init; }

    public required string Label { get; init; }

    public required string TokensLabel { get; init; }

    public int Tokens { get; init; }

    public double BarWidth { get; init; }

    public bool IsFirst { get; init; }

    public bool IsLast { get; init; }
}

public sealed partial class ContextOccupancyViewModel : ObservableObject
{
    internal const double RingDiameter = 16;
    internal const double RingStrokeThickness = 2.2;
    internal static readonly double RingCircumference = Math.PI * (RingDiameter - RingStrokeThickness);
    private const double DashEpsilon = 0.01;
    private const double TrackWidth = 256;

    [ObservableProperty]
    private bool isVisible;

    [ObservableProperty]
    private int percentUsed;

    [ObservableProperty]
    private string percentLabel = string.Empty;

    [ObservableProperty]
    private string usedCapacityLabel = string.Empty;

    [ObservableProperty]
    private DoubleCollection ringDashArray = FrozenDash(0);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCompactCtaEmphasized))]
    private ContextPressureLevel pressure = ContextPressureLevel.Normal;

    [ObservableProperty]
    private IReadOnlyList<ContextOccupancyCategoryRow> categories = Array.Empty<ContextOccupancyCategoryRow>();

    [ObservableProperty]
    private bool isCompacting;

    public IRelayCommand? CompactCommand { get; set; }

    public IRelayCommand? ClearCommand { get; set; }

    public bool IsCompactCtaEmphasized =>
        Pressure is ContextPressureLevel.Critical or ContextPressureLevel.Overflow;

    public void Apply(ContextBudgetSnapshot budget, ContextPressureLevel pressure)
    {
        if (!budget.HasOccupancy)
        {
            IsVisible = false;
            return;
        }

        IsVisible = true;
        Pressure = pressure;
        var usable = budget.UsablePromptWindow;
        var used = Math.Max(0, budget.DisplayedContentTokens);
        var utilization = usable > 0 ? (double)used / usable : 0;
        var percent = (int)Math.Clamp(Math.Round(utilization * 100), 0, 999);
        PercentUsed = percent;
        PercentLabel = Strings.Format("Chat_ContextMeterPercent", percent);
        UsedCapacityLabel = Strings.Format(
            "Chat_ContextMeterCapacity",
            TokenCountDisplay.FormatCompact(used),
            TokenCountDisplay.FormatCompact(usable));
        RingDashArray = FrozenDash(Math.Clamp(utilization, 0, 1) * RingCircumference);
        Categories = BuildCategories(budget.DisplayOccupancy, usable);
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

    internal static DoubleCollection FrozenDash(double filled)
    {
        var clamped = Math.Clamp(filled, 0, RingCircumference);
        // WPF StrokeDashArray values are multiples of StrokeThickness (not pixels),
        // so convert the pixel arc length to dash units before building the array.
        var dash = clamped / RingStrokeThickness;
        var gap = Math.Max(DashEpsilon, RingCircumference / RingStrokeThickness - dash);
        var collection = new DoubleCollection { dash, gap };
        collection.Freeze();
        return collection;
    }

    private static IReadOnlyList<ContextOccupancyCategoryRow> BuildCategories(
        ContextOccupancyBreakdown occupancy,
        int usableWindow)
    {
        var candidates = new (string Id, string LabelKey, int Tokens)[]
        {
            ("system", "Chat_ContextMeterSystem", occupancy.SystemPrompt),
            ("tools", "Chat_ContextMeterTools", occupancy.ToolDefinitions),
            ("rules", "Chat_ContextMeterRules", occupancy.Rules),
            ("skills", "Chat_ContextMeterSkills", occupancy.Skills),
            ("mcp", "Chat_ContextMeterMcp", occupancy.McpTools),
            ("subagent", "Chat_ContextMeterSubagent", occupancy.SubagentDefinitions),
            ("conversation", "Chat_ContextMeterMessages", occupancy.Conversation)
        };

        var visible = candidates.Where(item => item.Tokens > 0).ToList();
        if (visible.Count == 0)
        {
            return Array.Empty<ContextOccupancyCategoryRow>();
        }

        var denominator = Math.Max(usableWindow, occupancy.ContentTokens);
        var scale = denominator > 0 ? TrackWidth / denominator : 0;
        var rows = new List<ContextOccupancyCategoryRow>(visible.Count);
        for (var index = 0; index < visible.Count; index++)
        {
            var item = visible[index];
            rows.Add(new ContextOccupancyCategoryRow
            {
                Id = item.Id,
                Label = Strings.Get(item.LabelKey),
                TokensLabel = TokenCountDisplay.FormatCompact(item.Tokens),
                Tokens = item.Tokens,
                BarWidth = Math.Max(1, item.Tokens * scale),
                IsFirst = index == 0,
                IsLast = index == visible.Count - 1
            });
        }

        return rows;
    }
}
