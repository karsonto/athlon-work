using System.Windows;
using Athlon.Agent.App.Notifications;

namespace Athlon.Agent.App.Services;

public sealed class TaskPlanCompletionNotifier : ITaskPlanCompletionNotifier
{
    private TaskCompletionNoticeWindow? _activeWindow;

    public void NotifyAllTasksCompleted(string lastCompletedTaskContent)
    {
        var app = Application.Current;
        if (app is null)
        {
            return;
        }

        app.Dispatcher.InvokeAsync(() =>
        {
            _activeWindow?.Close();
            _activeWindow = new TaskCompletionNoticeWindow("任务计划已完成", lastCompletedTaskContent);
            _activeWindow.Closed += (_, _) => _activeWindow = null;
            _activeWindow.Show();
        });
    }
}
