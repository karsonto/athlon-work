using Athlon.Agent.Core;
using Athlon.Agent.Core.Browser;

namespace Athlon.Agent.Infrastructure.Browser;

public sealed class BrowserReadAriaTreeTool(IBrowserAutomationHost host) : IAgentTool, IBrowserTool
{
    public ToolDefinition Definition { get; } = new(
        "browser_read_aria_tree",
        "Read the page (or a subtree rooted at ref) as an ARIA semantic tree with refs for later interaction.",
        ToolSchema.Object()
            .Integer("depth", "Optional max tree depth.")
            .String("ref", "Optional root aria ref (e.g. aria_1).")
            .Build());

    public Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken cancellationToken = default) =>
        BrowserToolHelper.InvokeHostAsync(async ct =>
        {
            var args = BrowserToolHelper.BuildArgsJson(invocation, "depth", "ref");
            var json = await host.ExecuteAriaAsync("readAriaTree", args, ct).ConfigureAwait(false);
            return BrowserToolHelper.FromAriaJson(json, "ARIA tree");
        }, cancellationToken);
}

public sealed class BrowserFindAriaNodesTool(IBrowserAutomationHost host) : IAgentTool, IBrowserTool
{
    public ToolDefinition Definition { get; } = new(
        "browser_find_aria_nodes",
        "Find ARIA nodes by name, role, and/or text. Prefer this over reading the full tree when locating a control.",
        ToolSchema.Object()
            .String("name", "Accessible name substring or exact match.")
            .String("role", "ARIA/role name, e.g. button, textbox, link.")
            .String("text", "Visible text substring.")
            .String("scopeRef", "Optional aria ref to search under.")
            .Integer("limit", "Max candidates (1-10).")
            .Boolean("interactiveOnly", "Only interactive roles.")
            .String("intent", "Optional hint: click | type | select.")
            .Build());

    public Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken cancellationToken = default) =>
        BrowserToolHelper.InvokeHostAsync(async ct =>
        {
            var args = BrowserToolHelper.BuildArgsJson(
                invocation, "name", "role", "text", "scopeRef", "limit", "interactiveOnly", "intent");
            var json = await host.ExecuteAriaAsync("findAriaNodes", args, ct).ConfigureAwait(false);
            return BrowserToolHelper.FromAriaJson(json, "ARIA node candidates");
        }, cancellationToken);
}

public sealed class BrowserResolveAriaRefTool(IBrowserAutomationHost host) : IAgentTool, IBrowserTool
{
    public ToolDefinition Definition { get; } = new(
        "browser_resolve_aria_ref",
        "Validate an aria ref and return a short node summary.",
        ToolSchema.Object()
            .String("ref", "Full aria ref such as aria_1.", required: true, minLength: 1)
            .Build());

    public Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken cancellationToken = default) =>
        BrowserToolHelper.InvokeHostAsync(async ct =>
        {
            var args = BrowserToolHelper.BuildArgsJson(invocation, "ref");
            var json = await host.ExecuteAriaAsync("resolveAriaRef", args, ct).ConfigureAwait(false);
            return BrowserToolHelper.FromAriaJson(json, "ARIA ref resolved");
        }, cancellationToken);
}

public sealed class BrowserAriaInspectTool(IBrowserAutomationHost host) : IAgentTool, IBrowserTool
{
    public ToolDefinition Definition { get; } = new(
        "browser_aria_inspect",
        "Inspect an aria ref: role, name, states, value, nearby text, and available actions.",
        ToolSchema.Object()
            .String("ref", "Full aria ref such as aria_1.", required: true, minLength: 1)
            .Build());

    public Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken cancellationToken = default) =>
        BrowserToolHelper.InvokeHostAsync(async ct =>
        {
            var args = BrowserToolHelper.BuildArgsJson(invocation, "ref");
            var json = await host.ExecuteAriaAsync("ariaInspect", args, ct).ConfigureAwait(false);
            return BrowserToolHelper.FromAriaJson(json, "ARIA node inspected");
        }, cancellationToken);
}

public sealed class BrowserAriaInteractTool(IBrowserAutomationHost host) : IAgentTool, IBrowserTool
{
    public ToolDefinition Definition { get; } = new(
        "browser_aria_interact",
        "Perform one low-risk action on an aria ref: click, type, press, or selectOption. Verify with inspect/wait afterward.",
        ToolSchema.Object()
            .String("ref", "Full aria ref such as aria_1.", required: true, minLength: 1)
            .String("action", "click | type | press | selectOption", required: true, minLength: 1)
            .String("text", "Text for type action.")
            .String("key", "Key for press action.")
            .String("value", "Option value for selectOption.")
            .String("label", "Option label for selectOption.")
            .String("mode", "type mode: replace (default) or append.")
            .Build(),
        RequiresApproval: true);

    public Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken cancellationToken = default) =>
        BrowserToolHelper.InvokeHostAsync(async ct =>
        {
            var args = BrowserToolHelper.BuildArgsJson(
                invocation, "ref", "action", "text", "key", "value", "label", "mode");
            var json = await host.ExecuteAriaAsync("ariaInteract", args, ct).ConfigureAwait(false);
            return BrowserToolHelper.FromAriaJson(json, "ARIA interact completed");
        }, cancellationToken);
}

public sealed class BrowserWaitForAriaTool(IBrowserAutomationHost host) : IAgentTool, IBrowserTool
{
    public ToolDefinition Definition { get; } = new(
        "browser_wait_for_aria",
        "Wait for an aria condition: appear, disappear, stable, valueChanged, expandedChanged, or selectedChanged.",
        ToolSchema.Object()
            .String("ref", "Optional aria ref.")
            .String("name", "Optional accessible name.")
            .String("role", "Optional role.")
            .String("state", "appear | disappear | stable | valueChanged | expandedChanged | selectedChanged")
            .Integer("timeoutMs", "Timeout in ms (200-30000).")
            .Build());

    public Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken cancellationToken = default) =>
        BrowserToolHelper.InvokeHostAsync(async ct =>
        {
            var args = BrowserToolHelper.BuildArgsJson(
                invocation, "ref", "name", "role", "state", "timeoutMs");
            var json = await host.ExecuteAriaAsync("waitForAria", args, ct).ConfigureAwait(false);
            return BrowserToolHelper.FromAriaJson(json, "ARIA wait completed");
        }, cancellationToken);
}
