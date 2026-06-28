namespace Athlon.Agent.App.Services;

public interface ITaskPlanCompletionNotifier
{
    void NotifyTaskCompleted(string completedTaskContent);
}
