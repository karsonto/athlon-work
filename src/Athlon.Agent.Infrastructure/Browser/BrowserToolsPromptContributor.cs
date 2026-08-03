using System.Text;
using Athlon.Agent.Core.Browser;
using Athlon.Agent.Core.Prompt;

namespace Athlon.Agent.Infrastructure.Browser;

public sealed class BrowserToolsPromptContributor(IBrowserWorkspaceState browserWorkspaceState) : IRuntimeContextContributor
{
    public int Priority => 45;

    public void Append(StringBuilder builder, EnvironmentPromptContext context)
    {
        if (browserWorkspaceState.HasOpenBrowserTab)
        {
            builder.AppendLine("Browser workspace tools are available for the open Browser tab.");
            builder.AppendLine(
                "Prefer ARIA tools: browser_find_aria_nodes → browser_aria_inspect → browser_aria_interact → browser_wait_for_aria.");
            builder.AppendLine(
                "Use full refs exactly as returned (e.g. aria_1). Do not invent CSS selectors first.");
            builder.AppendLine("Use browser_read_aria_tree when you need page structure or a local subtree.");
            return;
        }

        builder.AppendLine(
            "No Browser tab is open yet. Call browser_navigate to open a page; ARIA page tools appear after a Browser tab exists.");
    }
}
