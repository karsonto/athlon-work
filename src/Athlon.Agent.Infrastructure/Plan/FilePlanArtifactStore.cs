using System.Text;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Plan;

namespace Athlon.Agent.Infrastructure.Plan;

/// <summary>
/// File-backed store for the approved plan, following the same layout conventions as
/// <see cref="Harness.FileSessionTaskListStore"/>: everything lives under the session directory
/// resolved by <see cref="IAgentRunContextAccessor.ResolveSessionDirectory"/>.
///
/// <para><c>plan.md</c> is the authoritative model-readable artifact (advertised to the model via
/// <c>ApprovedPlanRuntimeContributor</c> so it can <c>file_read</c> the full text), while
/// <c>plan.json</c> is a structured snapshot for UI/inspection.</para>
/// </summary>
public sealed class FilePlanArtifactStore(
    IAppPathProvider paths,
    IJsonFileStore jsonFileStore,
    IAgentRunContextAccessor runContextAccessor) : IPlanArtifactStore
{
    public const string MarkdownFileName = "plan.md";
    public const string SnapshotFileName = "plan.json";

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public async Task SaveAsync(
        string sessionId,
        string markdown,
        PlanRun? run,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(markdown))
        {
            return;
        }

        var sessionDir = ResolveSessionDirectory(sessionId);
        Directory.CreateDirectory(sessionDir);

        var markdownPath = Path.Combine(sessionDir, MarkdownFileName);
        await FileIoRetry.RunAsync(
            () => AtomicFile.WriteAllTextAsync(markdownPath, markdown, cancellationToken),
            cancellationToken).ConfigureAwait(false);

        var snapshot = BuildSnapshot(sessionId, markdown, run);
        await jsonFileStore
            .SaveAsync(Path.Combine(sessionDir, SnapshotFileName), snapshot, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<PlanArtifact?> LoadAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return null;
        }

        var sessionDir = ResolveSessionDirectory(sessionId);
        var markdownPath = Path.Combine(sessionDir, MarkdownFileName);
        var markdown = await TryReadMarkdownAsync(markdownPath, cancellationToken).ConfigureAwait(false);
        var snapshot = await TryLoadSnapshotAsync(
                Path.Combine(sessionDir, SnapshotFileName),
                cancellationToken)
            .ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(markdown) && snapshot is null)
        {
            return null;
        }

        var artifact = new PlanArtifact
        {
            SessionId = sessionId,
            Markdown = markdown,
            Run = snapshot,
            MarkdownPath = string.IsNullOrWhiteSpace(markdown) ? null : markdownPath
        };

        if (artifact.Run is not null && string.IsNullOrWhiteSpace(artifact.Run.PlanMarkdown))
        {
            artifact.Run.PlanMarkdown = markdown;
        }

        return artifact.HasContent ? artifact : null;
    }

    public Task ClearAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return Task.CompletedTask;
        }

        var sessionDir = ResolveSessionDirectory(sessionId);
        return FileIoRetry.RunAsync(
            () =>
            {
                TryDelete(Path.Combine(sessionDir, MarkdownFileName));
                TryDelete(Path.Combine(sessionDir, SnapshotFileName));
                return Task.CompletedTask;
            },
            cancellationToken);
    }

    private string ResolveSessionDirectory(string sessionId) =>
        runContextAccessor.ResolveSessionDirectory(paths.SessionsPath, sessionId);

    private static PlanRun BuildSnapshot(string sessionId, string markdown, PlanRun? run)
    {
        var source = run?.Clone();
        var todos = source is { Todos.Count: > 0 }
            ? source.Todos
            : PlanDocumentParser.ParseTodos(markdown).ToList();

        return new PlanRun
        {
            Id = string.IsNullOrWhiteSpace(source?.Id) ? "approved-plan" : source!.Id,
            SessionId = sessionId,
            Phase = PlanPhase.Done,
            Status = PlanRunStatuses.Approved,
            Goal = source?.Goal,
            Title = string.IsNullOrWhiteSpace(source?.Title)
                ? PlanDocumentParser.ParseTitle(markdown)
                : source!.Title,
            Overview = source?.Overview,
            PlanMarkdown = markdown,
            Todos = todos,
            CreatedAt = source?.CreatedAt ?? DateTimeOffset.UtcNow,
            UpdatedAt = source?.UpdatedAt ?? DateTimeOffset.UtcNow
        };
    }

    private static async Task<string?> TryReadMarkdownAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var text = await FileIoRetry
                .RunAsync(() => File.ReadAllTextAsync(path, Utf8NoBom, cancellationToken), cancellationToken)
                .ConfigureAwait(false);
            // Scrub a UTF-8 BOM if the file was hand-edited by a BOM-writing tool.
            return text.Length > 0 && text[0] == '\uFEFF' ? text[1..] : text;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private async Task<PlanRun?> TryLoadSnapshotAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            return await jsonFileStore.LoadAsync<PlanRun>(path, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // A corrupt snapshot must not break the context contributors; plan.md is enough to recover.
            return null;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception)
        {
            // Deletion is best-effort: a locked file retries on the next clear.
        }
    }
}
