using Athlon.Agent.Core;
using Athlon.Agent.Core.Compaction;
using Athlon.Agent.Core.Memory;
using Athlon.Agent.Core.Middleware;
using Athlon.Agent.Core.Prompt;

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
        IAgentRunContextAccessor runContextAccessor,
        AppSettings settings,
        IAppLogger logger,
        IPostTurnMemoryProcessor? memoryProcessor = null)
    {
        var (pipeline, compaction) = CreateMiddleware(
            preCompletionPipeline,
            storage,
            tokenEstimatorCalibrator,
            settings,
            logger,
            memoryProcessor);
        return new AgentRuntime(
            modelClient,
            storage,
            toolRouter,
            systemPromptOrchestrator,
            preCompletionPipeline,
            toolResultEvictor,
            tokenEstimatorCalibrator,
            new SessionUsageAccumulator(),
            new PromptPressureStore(),
            new SessionToolStormStore(),
            activeSessionContext,
            runContextAccessor,
            pipeline,
            compaction,
            settings,
            logger,
            memoryProcessor ?? new NoOpPostTurnMemoryProcessor());
    }

    public static (AgentTurnMiddlewarePipeline Pipeline, CompactionTurnMiddleware Compaction) CreateMiddleware(
        IPreCompletionPipeline preCompletionPipeline,
        IFileStorageService storage,
        ITokenEstimatorCalibrator tokenEstimatorCalibrator,
        AppSettings settings,
        IAppLogger logger,
        IPostTurnMemoryProcessor? memoryProcessor = null)
    {
        var compaction = new CompactionTurnMiddleware(
            preCompletionPipeline,
            tokenEstimatorCalibrator,
            new PromptPressureStore(),
            storage,
            settings);
        IAgentTurnMiddleware[] middlewares =
        [
            new ToolStormTurnMiddleware(settings, new SessionToolStormStore()),
            compaction,
            new PostTurnMemoryMiddleware(settings, memoryProcessor ?? new NoOpPostTurnMemoryProcessor(), logger)
        ];
        return (new AgentTurnMiddlewarePipeline(middlewares), compaction);
    }
}
