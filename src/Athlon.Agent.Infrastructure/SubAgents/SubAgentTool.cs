using System.Text;
using Athlon.Agent.Core;
using Athlon.Agent.Core.SubAgents;
using Microsoft.Extensions.DependencyInjection;

namespace Athlon.Agent.Infrastructure.SubAgents;

public sealed class SubAgentTool(
    AppSettings settings,
    IServiceProvider serviceProvider,
    IFileStorageService storage,
    ISubAgentSessionStore sessionStore,
    Lazy<ChildAgentToolRouter> childToolRouter,
    SubAgentSystemPromptOrchestrator subAgentPromptOrchestrator,
    IActiveAgentSessionContext activeSessionContext,
    IAgentRunContextAccessor runContextAccessor,
    IAppLogger logger) : IAgentTool, IExcludedFromChildAgentToolkit
{
    private readonly SubAgentSettings _subAgent = settings.SubAgent;
    private readonly IAppLogger _logger = logger.ForContext("SubAgentTool");

    public ToolDefinition Definition => BuildDefinition();

    public async Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!_subAgent.Enabled)
        {
            return ToolResult.Failure("Sub-agent disabled", "Sub-agent tool is disabled in settings.");
        }

        var parentSessionId = activeSessionContext.SessionId;
        if (string.IsNullOrWhiteSpace(parentSessionId))
        {
            return ToolResult.Failure("No parent session", "call_assistant requires an active parent agent session.");
        }

        if (_subAgent.MaxNestingDepth > 0 && SubAgentExecutionScope.CurrentDepth >= _subAgent.MaxNestingDepth)
        {
            return ToolResult.Failure(
                "Nesting limit",
                $"Sub-agent nesting depth limit ({_subAgent.MaxNestingDepth}) reached.");
        }

        if (!invocation.Arguments.TryGetValue("message", out var message) || string.IsNullOrWhiteSpace(message))
        {
            return ToolResult.Failure("Missing message", "Required parameter: message");
        }

        invocation.Arguments.TryGetValue("session_id", out var sessionIdArg);
        invocation.Arguments.TryGetValue("role", out var roleArg);

        var subSessionId = string.IsNullOrWhiteSpace(sessionIdArg)
            ? Guid.NewGuid().ToString("N")
            : sessionIdArg.Trim();

        SubAgentSessionBundle? bundle = null;
        if (!string.IsNullOrWhiteSpace(sessionIdArg))
        {
            bundle = await sessionStore.LoadAsync(parentSessionId, subSessionId, cancellationToken);
            if (bundle is null)
            {
                return ToolResult.Failure("Unknown session_id", $"No sub-agent session '{subSessionId}' for this parent.");
            }
        }

        var role = ResolveRole(bundle, roleArg);
        if (string.IsNullOrWhiteSpace(role))
        {
            return ToolResult.Failure(
                "Missing role",
                "Provide role when starting a new sub-agent session, or pass session_id for an existing session with saved role.");
        }

        var session = bundle?.Session ?? await CreateSubSessionAsync(parentSessionId, subSessionId, cancellationToken);
        bundle = new SubAgentSessionBundle(session, role);

        var ignorePatterns = ResolveIgnorePatterns(session);
        var workspaceRoot = string.IsNullOrWhiteSpace(session.ActiveWorkspace)
            ? runContextAccessor.Current?.WorkspaceRoot
            : Path.GetFullPath(session.ActiveWorkspace);
        var parentRunContext = runContextAccessor.Current
            ?? AgentRunContext.CreateRoot(
                new AgentSession(parentSessionId, string.Empty, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, workspaceRoot, null, settings.Model.ModelName, []),
                Guid.NewGuid().ToString("N"),
                childToolRouter.Value,
                subAgentPromptOrchestrator,
                ignorePatterns);
        var childContext = parentRunContext.CreateChild(
            subSessionId,
            childToolRouter.Value,
            subAgentPromptOrchestrator,
            role,
            new AgentLoopOptions { MaxModelToolRounds = _subAgent.MaxToolRounds },
            workspaceRoot,
            ignorePatterns);

        using var depthScope = SubAgentExecutionScope.Enter();
        using var runScope = runContextAccessor.Push(childContext);
        using var sessionScope = activeSessionContext.Enter(subSessionId);
        using var workspaceScope = SessionWorkspaceScope.Enter(childContext.WorkspaceRoot, childContext.WorkspaceIgnorePatterns);

        _logger.Information(
            "Starting sub-agent run parent={ParentId} sub={SubId} continue={Continue}",
            parentSessionId,
            subSessionId,
            bundle.Session.Messages.Count > 0);

        try
        {
            var orchestrator = serviceProvider.GetRequiredService<IAgentOrchestrator>();
            session = await orchestrator.SendAsync(session, message.Trim(), null, null, cancellationToken);
            bundle = bundle with { Session = session };
            await sessionStore.SaveAsync(parentSessionId, subSessionId, bundle, cancellationToken);

            var responseText = ExtractLastAssistantText(session) ?? "(Sub-agent finished without assistant text.)";
            var content = BuildHandoffContent(subSessionId, session, responseText);

            return ToolResult.Success($"Sub-agent completed (session_id={subSessionId})", content);
        }
        catch (OperationCanceledException)
        {
            await sessionStore.SaveAsync(parentSessionId, subSessionId, bundle, CancellationToken.None);
            throw;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Sub-agent run failed parent={ParentId} sub={SubId}", parentSessionId, subSessionId);
            return ToolResult.Failure("Sub-agent failed", ex.Message);
        }
    }

    private static string ResolveRole(SubAgentSessionBundle? bundle, string? roleArg)
    {
        if (!string.IsNullOrWhiteSpace(roleArg))
        {
            return roleArg.Trim();
        }

        return bundle?.Role?.Trim() ?? string.Empty;
    }

    private async Task<AgentSession> CreateSubSessionAsync(
        string parentSessionId,
        string subSessionId,
        CancellationToken cancellationToken)
    {
        var parent = await storage.LoadSessionAsync(parentSessionId, cancellationToken);
        var workspace = parent?.ActiveWorkspace ?? SessionWorkspaceScope.CurrentState?.RootPath;
        var activeSkill = parent?.ActiveSkill;
        return new AgentSession(
            subSessionId,
            "Sub-agent",
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            workspace,
            activeSkill,
            parent?.ModelName ?? settings.Model.ModelName,
            Array.Empty<ChatMessage>());
    }

    private IReadOnlyList<string> ResolveIgnorePatterns(AgentSession session)
    {
        if (!string.IsNullOrWhiteSpace(session.ActiveWorkspace))
        {
            var fullPath = Path.GetFullPath(session.ActiveWorkspace);
            var match = settings.Workspaces.FirstOrDefault(workspace =>
                !string.IsNullOrWhiteSpace(workspace.RootPath)
                && string.Equals(Path.GetFullPath(workspace.RootPath), fullPath, StringComparison.OrdinalIgnoreCase));
            return WorkspaceIgnoreResolver.Resolve(
                workspacePatterns: match?.IgnorePatterns,
                globalPatterns: settings.WorkspaceIgnore.DirectoryNames);
        }

        var configuredWorkspace = settings.Workspaces.FirstOrDefault(workspace => !string.IsNullOrWhiteSpace(workspace.RootPath));
        return WorkspaceIgnoreResolver.Resolve(
            workspacePatterns: configuredWorkspace?.IgnorePatterns,
            globalPatterns: settings.WorkspaceIgnore.DirectoryNames);
    }

    private static string? ExtractLastAssistantText(AgentSession session)
    {
        for (var index = session.Messages.Count - 1; index >= 0; index--)
        {
            var message = session.Messages[index];
            if (message.Role == MessageRole.Assistant && !string.IsNullOrWhiteSpace(message.Content))
            {
                return message.Content;
            }
        }

        return null;
    }

    private string BuildHandoffContent(string subSessionId, AgentSession session, string responseText)
    {
        var builder = new StringBuilder();
        builder.AppendLine("## Result");
        builder.AppendLine(responseText);
        builder.AppendLine();
        builder.AppendLine("## Session");
        builder.AppendLine($"session_id: {subSessionId}");
        builder.AppendLine();
        builder.AppendLine("## Trace (abbreviated)");
        builder.Append(ExtractToolTrace(session, _subAgent.MaxHandoffChars));
        return builder.ToString().TrimEnd();
    }

    private static string ExtractToolTrace(AgentSession session, int maxChars)
    {
        var lines = new List<string>();
        foreach (var message in session.Messages)
        {
            if (message.Role != MessageRole.Tool)
            {
                continue;
            }

            var line = SummarizeToolMessage(message.Content);
            if (!string.IsNullOrWhiteSpace(line))
            {
                lines.Add($"- {line}");
            }
        }

        if (lines.Count == 0)
        {
            return "- (no tool calls)";
        }

        var text = string.Join(Environment.NewLine, lines);
        if (text.Length <= maxChars)
        {
            return text;
        }

        return text[..maxChars] + Environment.NewLine + $"... ({lines.Count} tool results; trace truncated)";
    }

    private static string SummarizeToolMessage(string content)
    {
        var lines = content.Replace("\r\n", "\n").Split('\n');
        var toolLine = lines.FirstOrDefault(line => line.StartsWith("Tool `", StringComparison.Ordinal)) ?? "tool";
        var summaryLine = lines.FirstOrDefault(line => line.StartsWith("Summary:", StringComparison.OrdinalIgnoreCase));
        return summaryLine is null ? toolLine.Trim() : $"{toolLine.Trim()} → {summaryLine["Summary:".Length..].Trim()}";
    }

    private ToolDefinition BuildDefinition()
    {
        var toolName = string.IsNullOrWhiteSpace(_subAgent.ToolName) ? "call_assistant" : _subAgent.ToolName.Trim();
        return new ToolDefinition(
            toolName,
            _subAgent.Description,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["role"] = "Who the child agent is: responsibilities, boundaries, and output style. Required for a new session_id; optional when continuing (updates saved role).",
                ["message"] = "Task instruction for this sub-agent turn.",
                ["session_id"] = "Optional. Omit to start a new sub-session; pass the id from a prior result to continue."
            },
            Group: ToolGroup.SubAgent);
    }
}
