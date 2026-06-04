using Athlon.Agent.Core;
using Athlon.Agent.Infrastructure;
using Athlon.Agent.Infrastructure.SubAgents;
using Microsoft.Extensions.DependencyInjection;

namespace Athlon.Agent.Tests;

public sealed class AppStartupIntegrationTest
{
    [Fact]
    public void AddAthlonInfrastructure_ResolvesRuntimeAndSubAgentTool_WithoutCycle()
    {
        var services = new ServiceCollection();
        services.AddAthlonInfrastructure();

        var provider = services.BuildServiceProvider();
        var runtime = provider.GetRequiredService<IAgentRuntime>();
        var orchestrator = provider.GetRequiredService<IAgentOrchestrator>();
        var router = provider.GetRequiredService<IToolRouter>();
        var subAgent = provider.GetRequiredService<SubAgentTool>();

        Assert.NotNull(runtime);
        Assert.NotNull(orchestrator);
        Assert.NotNull(router);
        Assert.Contains(provider.GetServices<IAgentTool>(), tool => ReferenceEquals(tool, subAgent));

        if (provider is IAsyncDisposable asyncDisposable)
        {
            asyncDisposable.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }
}
