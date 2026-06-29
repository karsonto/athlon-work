using Athlon.Agent.Core;

namespace Athlon.Agent.Infrastructure;

public sealed class StartupLog(IAppPathProvider paths) : IStartupLog
{
    public void Write(string message)
    {
        paths.EnsureCreated();
        File.AppendAllText(
            Path.Combine(paths.LogsPath, "startup.log"),
            $"{AppTimeZone.Now:O} {message}{Environment.NewLine}");
    }
}
