using System.Windows;
using Athlon.Agent.App.Notifications;

using Athlon.Agent.App.Resources;

namespace Athlon.Agent.App.Services;

public sealed class TaskPlanCompletionNotifier : ITaskPlanCompletionNotifier
{
    private TaskCompletionNoticeWindow? _activeWindow;

    public void NotifyTaskCompleted(string completedTaskContent)
    {
        var app = Application.Current;
        if (app is null)
        {
            return;
        }

        app.Dispatcher.InvokeAsync(() =>
        {
            _activeWindow?.Close();
            _activeWindow = new TaskCompletionNoticeWindow(Strings.Get("Notification_TaskCompleteTitle"), completedTaskContent);
            _activeWindow.Closed += (_, _) => _activeWindow = null;
            _activeWindow.Show();
        });
    }
}
