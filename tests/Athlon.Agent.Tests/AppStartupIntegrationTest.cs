using Athlon.Agent.Core;
using Athlon.Agent.Infrastructure;
using Athlon.Agent.Infrastructure.SubAgents;
using Microsoft.Extensions.DependencyInjection;

namespace Athlon.Agent.Tests;

public sealed class AppStartupIntegrationTest
{
    [Fact]
    public void AddAthlonInfrastructure_ResolvesRuntimeAndSessionsSpawnTool_WithoutCycle()
    {
        var services = new ServiceCollection();
        services.AddAthlonInfrastructure();

        var provider = services.BuildServiceProvider();
        var runtime = provider.GetRequiredService<IAgentRuntime>();
        var orchestrator = provider.GetRequiredService<IAgentOrchestrator>();
        var router = provider.GetRequiredService<IToolRouter>();
        var spawnTool = provider.GetServices<IAgentTool>()
            .OfType<SessionsSpawnTool>()
            .Single();

        Assert.NotNull(runtime);
        Assert.NotNull(orchestrator);
        Assert.NotNull(router);
        Assert.Equal("sessions_spawn", spawnTool.Definition.Name);

        if (provider is IAsyncDisposable asyncDisposable)
        {
            asyncDisposable.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }
}
