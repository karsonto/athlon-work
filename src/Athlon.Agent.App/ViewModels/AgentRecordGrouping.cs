using Athlon.Agent.App.Localization;
using Athlon.Agent.App.Resources;
using Athlon.Agent.Core;

namespace Athlon.Agent.App.ViewModels;

public static class AgentRecordGrouping
{
    public const string TodayKey = "today";
    public const string Last7DaysKey = "last7";
    public const string EarlierKey = "earlier";

    public static IReadOnlyList<AgentRecordGroupViewModel> Build(
        IReadOnlyList<SessionIndexEntry> entries,
        string activeSessionId,
        Func<string, bool> isRunning,
        Action<string>? stopSession)
    {
        var today = AppTimeZone.Today;
        var last7Start = today.AddDays(-7);

        var todayGroup = new AgentRecordGroupViewModel(TodayKey, Strings.Get("RecordGroup_Today"), isExpandedByDefault: true);
        var last7Group = new AgentRecordGroupViewModel(Last7DaysKey, Strings.Get("RecordGroup_Last7Days"), isExpandedByDefault: true);
        var earlierGroup = new AgentRecordGroupViewModel(EarlierKey, Strings.Get("RecordGroup_Earlier"), isExpandedByDefault: false);

        foreach (var entry in entries)
        {
            var item = new SessionHistoryItemViewModel(
                entry,
                entry.Id == activeSessionId,
                isRunning(entry.Id),
                stopSession);

            var localDate = AppTimeZone.ToChinaDate(entry.UpdatedAt);
            if (localDate == today)
            {
                todayGroup.Items.Add(item);
            }
            else if (localDate >= last7Start)
            {
                last7Group.Items.Add(item);
            }
            else
            {
                earlierGroup.Items.Add(item);
            }
        }

        return [todayGroup, last7Group, earlierGroup];
    }
}
