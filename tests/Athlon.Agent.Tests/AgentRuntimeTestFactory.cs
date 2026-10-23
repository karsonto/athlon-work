using Athlon.Agent.Core;
using Athlon.Agent.Core.Compaction;
using Athlon.Agent.Core.Memory;
using Athlon.Agent.Core.Middleware;
using Athlon.Agent.Core.Prompt;
using Athlon.Agent.Core.BehaviorReport;
using Athlon.Agent.Core.RuntimeDiagnostics;

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
        var sessionUsage = new SessionUsageAccumulator();
        var promptPressure = new PromptPressureStore();
        var diagnosticSink = new NullRuntimeDiagnosticEventSink();
        var (toolPipeline, turnCoordinator, bridge, transcriptWriter) = CreateCollaborators(
            modelClient,
            storage,
            toolRouter,
            toolResultEvictor,
            tokenEstimatorCalibrator,
            sessionUsage,
            promptPressure,
            runContextAccessor,
            settings,
            logger,
            diagnosticSink);
        return CreateRuntime(
            storage,
            toolRouter,
            systemPromptOrchestrator,
            sessionUsage,
            activeSessionContext,
            runContextAccessor,
            pipeline,
            compaction,
            settings,
            logger,
            transcriptWriter,
            toolPipeline,
            turnCoordinator,
            bridge);
    }

    public static AgentRuntime CreateRuntimeFromMiddleware(
        IAgentModelClient modelClient,
        IFileStorageService storage,
        IToolRouter toolRouter,
        ISystemPromptOrchestrator systemPromptOrchestrator,
        IToolResultEvictor toolResultEvictor,
        ITokenEstimatorCalibrator tokenEstimatorCalibrator,
        IActiveAgentSessionContext activeSessionContext,
        IAgentRunContextAccessor runContextAccessor,
        AgentTurnMiddlewarePipeline pipeline,
        CompactionTurnMiddleware compaction,
        AppSettings settings,
        IAppLogger logger,
        IRuntimeDiagnosticEventSink? runtimeDiagnosticEventSink = null)
    {
        var sessionUsage = new SessionUsageAccumulator();
        var promptPressure = new PromptPressureStore();
        var diagnosticSink = runtimeDiagnosticEventSink ?? new NullRuntimeDiagnosticEventSink();
        var (toolPipeline, turnCoordinator, bridge, transcriptWriter) = CreateCollaborators(
            modelClient,
            storage,
            toolRouter,
            toolResultEvictor,
            tokenEstimatorCalibrator,
            sessionUsage,
            promptPressure,
            runContextAccessor,
            settings,
            logger,
            diagnosticSink);
        return CreateRuntime(
            storage,
            toolRouter,
            systemPromptOrchestrator,
            sessionUsage,
            activeSessionContext,
            runContextAccessor,
            pipeline,
            compaction,
            settings,
            logger,
            transcriptWriter,
            toolPipeline,
            turnCoordinator,
            bridge);
    }

    public static (
        ToolInvocationPipeline ToolPipeline,
        AgentTurnCoordinator TurnCoordinator,
        AgentTurnForceCompactBridge ForceCompactBridge,
        ImmediateConversationTranscriptWriter TranscriptWriter) CreateCollaborators(
        IAgentModelClient modelClient,
        IFileStorageService storage,
        IToolRouter toolRouter,
        IToolResultEvictor toolResultEvictor,
        ITokenEstimatorCalibrator tokenEstimatorCalibrator,
        ISessionUsageAccumulator sessionUsageAccumulator,
        IPromptPressureStore promptPressureStore,
        IAgentRunContextAccessor runContextAccessor,
        AppSettings settings,
        IAppLogger logger,
        IRuntimeDiagnosticEventSink runtimeDiagnosticEventSink,
        IEventManager? eventManager = null)
    {
        var bridge = new AgentTurnForceCompactBridge();
        var transcriptWriter = new ImmediateConversationTranscriptWriter(storage);
        var toolPipeline = new ToolInvocationPipeline(
            storage,
            toolResultEvictor,
            () => runContextAccessor.Current?.ToolRouter ?? toolRouter,
            runContextAccessor,
            () => settings.ToolPermissions.ApprovalEnabled,
            logger,
            eventManager,
            runtimeDiagnosticEventSink);
        var turnCoordinator = new AgentTurnCoordinator(
            modelClient,
            tokenEstimatorCalibrator,
            sessionUsageAccumulator,
            promptPressureStore,
            storage,
            settings,
            runContextAccessor,
            bridge,
            logger,
            eventManager,
            runtimeDiagnosticEventSink);
        return (toolPipeline, turnCoordinator, bridge, transcriptWriter);
    }

    public static AgentRuntime CreateRuntime(
        IFileStorageService storage,
        IToolRouter toolRouter,
        ISystemPromptOrchestrator systemPromptOrchestrator,
        ISessionUsageAccumulator sessionUsageAccumulator,
        IActiveAgentSessionContext activeSessionContext,
        IAgentRunContextAccessor runContextAccessor,
        AgentTurnMiddlewarePipeline pipeline,
        CompactionTurnMiddleware compaction,
        AppSettings settings,
        IAppLogger logger,
        ImmediateConversationTranscriptWriter transcriptWriter,
        ToolInvocationPipeline toolPipeline,
        AgentTurnCoordinator turnCoordinator,
        AgentTurnForceCompactBridge forceCompactBridge,
        IEventManager? eventManager = null)
    {
        var runtime = new AgentRuntime(
            storage,
            toolRouter,
            systemPromptOrchestrator,
            sessionUsageAccumulator,
            activeSessionContext,
            runContextAccessor,
            pipeline,
            compaction,
            settings,
            logger,
            transcriptWriter,
            toolPipeline,
            turnCoordinator,
            eventManager);
        forceCompactBridge.Attach(runtime);
        return runtime;
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
            new PostTurnMemoryMiddleware(
                memoryProcessor ?? new NoOpPostTurnMemoryProcessor(),
                logger)
        ];
        return (new AgentTurnMiddlewarePipeline(middlewares), compaction);
    }
}
