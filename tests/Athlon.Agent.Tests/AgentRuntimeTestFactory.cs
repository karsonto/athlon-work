using Athlon.Agent.Core;
using Athlon.Agent.Core.Compaction;
using Athlon.Agent.Core.Memory;

namespace Athlon.Agent.Tests;

internal static class AgentRuntimeTestFactory
{
    public static AgentRuntime Create(
        IAgentModelClient modelClient,
        IFileStorageService storage,
        IToolRouter toolRouter,
        ISystemPromptOrchestrator systemPromptOrchestrator,
        IPreCompletionPipeline preCompletionPipeline,
        IToolResultEvictor toolResultEvictor,
        ITokenEstimatorCalibrator tokenEstimatorCalibrator,
        IActiveAgentSessionContext activeSessionContext,
        AppSettings settings,
        IAppLogger logger,
        IPostTurnMemoryProcessor? memoryProcessor = null) =>
        new(
            modelClient,
            storage,
            toolRouter,
            systemPromptOrchestrator,
            preCompletionPipeline,
            toolResultEvictor,
            tokenEstimatorCalibrator,
            new SessionUsageAccumulator(),
            new PromptPressureStore(),
            activeSessionContext,
            settings,
            logger,
            memoryProcessor ?? new NoOpPostTurnMemoryProcessor());
}
