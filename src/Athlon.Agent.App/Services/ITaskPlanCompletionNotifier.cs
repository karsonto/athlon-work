namespace Athlon.Agent.App.Services;

public interface ITaskPlanCompletionNotifier
{
    void NotifyAllTasksCompleted(string lastCompletedTaskContent);
}
