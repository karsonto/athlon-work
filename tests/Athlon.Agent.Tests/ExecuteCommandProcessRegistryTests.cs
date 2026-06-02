using System.Diagnostics;
using Athlon.Agent.Infrastructure;

namespace Athlon.Agent.Tests;

public sealed class ExecuteCommandProcessRegistryTests
{
    [Fact]
    public void RegisterUnregister_TracksProcess()
    {
        var registry = new ExecuteCommandProcessRegistry();
        using var process = Process.Start(new ProcessStartInfo("cmd.exe", "/c exit")
        {
            CreateNoWindow = true,
            UseShellExecute = false
        });
        Assert.NotNull(process);

        registry.Register(process);
        registry.Unregister(process);
        registry.KillAll();
    }

    [Fact]
    public void KillAll_DoesNotThrow_WhenEmpty()
    {
        var registry = new ExecuteCommandProcessRegistry();
        registry.KillAll();
    }
}
