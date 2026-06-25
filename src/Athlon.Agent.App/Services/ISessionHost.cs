namespace Athlon.Agent.App.Services;

public interface ISessionHost
{
    Task OpenSessionByIdAsync(string sessionId);
}
