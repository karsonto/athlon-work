using Athlon.Agent.Core;
using Athlon.Agent.Core.Plan;

namespace Athlon.Agent.Infrastructure.Plan;

public sealed class CreatePlanTool(IPlanNotebook planNotebook, IActiveAgentSessionContext sessionContext) : IAgentTool
{
    private const string SubtasksExample =
        """
        [
          {
            "name": "Extend plan schema",
            "description": "Add overview, architecture, mermaid, and files[] to create_plan in src/Athlon.Agent.Core/Plan and Infrastructure/Plan.",
            "expected_outcome": "create_plan accepts rich fields; PlanNotebook validates min lengths; plan.md renders Cursor-style sections.",
            "files": ["src/Athlon.Agent.Core/Plan/AgentPlan.cs", "src/Athlon.Agent.Infrastructure/Plan/CreatePlanTool.cs"]
          },
          {
            "name": "Update prompts and tests",
            "description": "Refresh PlanModePolicySection with a full example; fix PlanNotebookTests and PlanToolTests fixtures to meet min length.",
            "expected_outcome": "dotnet test passes for plan-related tests.",
            "files": ["src/Athlon.Agent.Core/Prompt/PlanModePolicySection.cs", "tests/Athlon.Agent.Tests/PlanNotebookTests.cs"]
          }
        ]
        """;

    public ToolDefinition Definition { get; } = new(
        "create_plan",
        "Create a detailed implementation plan (Cursor-style spec). Replaces any existing session plan. "
        + "Use after researching the codebase. Each subtask needs concrete files and measurable acceptance.",
        new Dictionary<string, string>
        {
            ["name"] = "Short plan title",
            ["description"] = "One-sentence summary for quick reference",
            ["expected_outcome"] = "Measurable outcome for the entire plan",
            ["overview"] =
                "Required Markdown: background, goals, constraints, and key technical decisions (min ~200 chars).",
            ["architecture"] = "Optional Markdown: components, data flow, trade-offs",
            ["mermaid"] = "Optional Mermaid diagram source (no fences), e.g. flowchart or stateDiagram-v2",
            ["testing_strategy"] = "Optional Markdown: how to verify the work",
            ["out_of_scope"] = "Optional Markdown: what this plan explicitly excludes",
            ["subtasks"] = "JSON array: name, description, expected_outcome, optional files[]. Example: " + SubtasksExample
        });

    public Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var sessionId = PlanToolBase.RequireSessionId(sessionContext);
        if (sessionId is null)
        {
            return Task.FromResult(PlanToolBase.MissingSession());
        }

        if (!ToolArguments.TryGetRequired(invocation, "name", out var name, out var error))
        {
            return Task.FromResult(error);
        }

        if (!ToolArguments.TryGetRequired(invocation, "description", out var description, out error))
        {
            return Task.FromResult(error);
        }

        if (!ToolArguments.TryGetRequired(invocation, "expected_outcome", out var expectedOutcome, out error))
        {
            return Task.FromResult(error);
        }

        if (!ToolArguments.TryGetRequired(invocation, "overview", out var overview, out error))
        {
            return Task.FromResult(error);
        }

        if (!ToolArguments.TryGetRequired(invocation, "subtasks", out var subtasksJson, out error))
        {
            return Task.FromResult(error);
        }

        if (!SubTaskJsonParser.TryParse(subtasksJson, out var subtasks, out var parseError))
        {
            return Task.FromResult(ToolResult.Failure("Invalid subtasks", parseError));
        }

        invocation.Arguments.TryGetValue("architecture", out var architecture);
        invocation.Arguments.TryGetValue("mermaid", out var mermaid);
        invocation.Arguments.TryGetValue("testing_strategy", out var testingStrategy);
        invocation.Arguments.TryGetValue("out_of_scope", out var outOfScope);

        var result = planNotebook.CreatePlan(
            sessionId,
            new CreatePlanRequest(
                name,
                description,
                expectedOutcome,
                overview,
                subtasks,
                architecture,
                mermaid,
                testingStrategy,
                outOfScope));

        return Task.FromResult(
            result.Success
                ? ToolResult.Success(result.Message, result.Message)
                : ToolResult.Failure("Create plan failed", result.Message));
    }
}
