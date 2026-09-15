using System.Text;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Harness;
using Athlon.Agent.Core.Plan;
using Athlon.Agent.Core.Prompt;
using Athlon.Agent.Core.Threading;

namespace Athlon.Agent.Infrastructure.Prompt;

/// <summary>
/// Re-injects the approved plan on every model round while the session is executing it.
///
/// <para>Without this, the plan's only home is the approved-plan user message. Once the user sends
/// another message that turn falls out of the compaction keep-window, and
/// <c>SemanticCutoffPlanner</c> reduces it to a ~240 character appendix — so the model keeps
/// "following" a plan whose details it can no longer read. This contributor makes the plan a
/// first-class part of the runtime context instead.</para>
///
/// <para>Runs before <see cref="TaskListPromptContributor"/> (35) so the task list lands directly
/// after the plan it was seeded from.</para>
/// </summary>
public sealed class ApprovedPlanRuntimeContributor(
    IPlanArtifactStore planArtifactStore,
    ISessionHarnessState harnessState,
    IAgentRunContextAccessor runContextAccessor) : IRuntimeContextContributor
{
    /// <summary>Upper bound on the inlined plan body; the model can file_read the rest.</summary>
    private const int MaxInlineChars = 4000;

    private const int MaxSteps = 12;

    private const int MaxAcceptance = 8;

    public int Priority => 30;

    public void Append(StringBuilder builder, EnvironmentPromptContext context)
    {
        if (PromptModeHelper.IsChatOnly(context))
        {
            return;
        }

        // Plan mode has its own contributor (including the full draft text); never double-inject.
        if (context.AgentMode == SessionAgentMode.Plan
            || harnessState.GetMode(context.Session.Id) == SessionAgentMode.Plan)
        {
            return;
        }

        var sessionId = runContextAccessor.Current?.SessionId ?? context.Session.Id;
        var artifact = SyncOverAsync.Run(() => planArtifactStore.LoadAsync(sessionId));
        if (artifact is null || !artifact.HasContent)
        {
            return;
        }

        var markdown = artifact.Markdown
            ?? artifact.Run?.PlanMarkdown
            ?? string.Empty;
        var run = artifact.Run;
        var todos = run is { Todos.Count: > 0 }
            ? run.Todos
            : PlanDocumentParser.ParseTodos(markdown).ToList();

        builder.AppendLine();
        builder.AppendLine("## Approved Session Plan");
        builder.AppendLine();
        builder.AppendLine(
            "The user approved this plan and it is still the source of truth for this session. "
            + "Keep implementing it end to end; do not wait for another user message between steps, and do not "
            + "re-plan unless the user asks for a change.");
        builder.AppendLine();

        var title = !string.IsNullOrWhiteSpace(run?.Title)
            ? run!.Title
            : PlanDocumentParser.ParseTitle(markdown);
        if (!string.IsNullOrWhiteSpace(title))
        {
            builder.AppendLine($"Plan title: {title}");
        }

        // Local workspaces can reach the session directory via file_read, so advertise the path and
        // keep the inline copy bounded. SSH workspaces cannot read it, so inline as much as possible.
        var canReadArtifactFile = context.WorkspaceKind == WorkspaceKind.Local
            && !string.IsNullOrWhiteSpace(artifact.MarkdownPath);
        if (canReadArtifactFile)
        {
            builder.AppendLine($"Full plan file: {artifact.MarkdownPath}");
            builder.AppendLine(
                "Read that file when you need sections this summary omits (for example exact commands, paths, or code).");
        }
        else if (!string.IsNullOrWhiteSpace(markdown))
        {
            builder.AppendLine("Full plan document:");
            builder.AppendLine();
            builder.AppendLine(Truncate(markdown, MaxInlineChars));
        }

        if (todos.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("Implementation steps (mirrored in the task list):");
            foreach (var todo in todos.Take(MaxSteps))
            {
                builder.AppendLine($"- {todo.Id}: {todo.Content}");
            }
        }

        var acceptance = ExtractSection(markdown, "Acceptance");
        if (acceptance.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("Acceptance criteria:");
            foreach (var item in acceptance.Take(MaxAcceptance))
            {
                builder.AppendLine($"- {item}");
            }
        }

        builder.AppendLine();
    }

    /// <summary>Collects bullet/checkbox entries under a <c>##</c> heading matching one of the names.</summary>
    internal static IReadOnlyList<string> ExtractSection(string? markdown, params string[] headingNames)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return [];
        }

        var items = new List<string>();
        var inSection = false;
        foreach (var rawLine in markdown.Split(['\r', '\n']))
        {
            var line = rawLine.Trim();
            if (line.StartsWith("##", StringComparison.Ordinal))
            {
                var heading = line.TrimStart('#').Trim();
                inSection = headingNames.Any(name =>
                    string.Equals(heading, name, StringComparison.OrdinalIgnoreCase));
                continue;
            }

            if (!inSection || line.Length == 0)
            {
                continue;
            }

            var content = StripBullet(line);
            if (content.Length > 0)
            {
                items.Add(content);
            }
        }

        return items;
    }

    private static string StripBullet(string line)
    {
        var text = line;
        if (text.StartsWith("- [ ]", StringComparison.Ordinal)
            || text.StartsWith("- [x]", StringComparison.OrdinalIgnoreCase))
        {
            return text[5..].Trim();
        }

        if (text.StartsWith("- ", StringComparison.Ordinal)
            || text.StartsWith("* ", StringComparison.Ordinal)
            || text.StartsWith("+ ", StringComparison.Ordinal))
        {
            return text[2..].Trim();
        }

        return text;
    }

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..max] + "\n[... truncated; read the full plan file for the rest ...]";
}
