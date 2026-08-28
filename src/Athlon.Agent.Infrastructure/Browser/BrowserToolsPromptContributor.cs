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
            // Aligned with edge-plugin browser-agent rules: read → find → inspect → act → verify.
            builder.AppendLine("Browser workspace tools are available for the open Browser tab.");
            builder.AppendLine("Rules:");
            builder.AppendLine("1. Prefer find then act: browser_find_aria_nodes → browser_aria_inspect → browser_aria_interact → browser_wait_for_aria.");
            builder.AppendLine("2. browser_find_aria_nodes requires at least one of name, role, or text (limit alone is invalid).");
            builder.AppendLine("3. For form fields, prefer role=\"field\" or role=\"textbox\" with name/text; for buttons use text+role=\"button\".");
            builder.AppendLine("4. Use browser_read_aria_tree with filter=\"interactive\" when you need page structure; avoid repeatedly reading the full tree.");
            builder.AppendLine("5. Use full refs exactly as returned (e.g. aria_1). Do not invent CSS selectors first.");
            builder.AppendLine("6. One action tool at a time; verify after each action before the next step.");
            builder.AppendLine("7. For API or page errors: browser_network_list → browser_network_get (one requestId at a time); use browser_console_read for JS errors.");
            builder.AppendLine("8. UI interaction uses browser_aria_*; network and console analysis uses browser_network_* and browser_console_read.");
            return;
        }

        builder.AppendLine(
            "No Browser tab is open yet. Call browser_navigate to open a page; after it succeeds, ARIA page tools unlock on the next model step in the same turn.");
    }
}
