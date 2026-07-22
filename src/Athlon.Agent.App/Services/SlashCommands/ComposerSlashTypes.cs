using Athlon.Agent.App.Services;
using Athlon.Agent.Core;

namespace Athlon.Agent.App.Services.SlashCommands;

public enum ComposerCompletionItemKind
{
    File,
    Folder,
    Skill,
    Mcp,
    SlashCommand
}

public enum ComposerSlashIntent
{
    Discovery,
    ExactCommand
}

public sealed record ComposerSlashQuery(
    int TriggerStart,
    int QueryEndExclusive,
    string Query,
    ComposerSlashIntent Intent);

public sealed record ComposerSlashCommandResult(
    bool Handled,
    string? StatusMessage = null);

public sealed class ComposerSlashCommandContext
{
    public required AgentSession Session { get; init; }
    public required bool IsBusy { get; init; }
    public required bool IsCompacting { get; init; }
    public required int MessageCount { get; init; }
    public required Func<CancellationToken, Task<ManualCompactionResult>> CompactAsync { get; init; }
    public required Func<Task> ClearContextAsync { get; init; }
    public required Action<string> SetStatus { get; init; }
    public required Action NotifyCommandStatesChanged { get; init; }
}

public interface IComposerSlashCommand
{
    string Name { get; }
    string Description { get; }
    bool IsAvailable(ComposerSlashCommandContext context);
    ValueTask<ComposerSlashCommandResult> ExecuteAsync(
        ComposerSlashCommandContext context,
        CancellationToken cancellationToken = default);
}

public interface IComposerSlashCommandRegistry
{
    IReadOnlyList<IComposerSlashCommand> All { get; }
    bool TryGetExact(string name, out IComposerSlashCommand? command);
    IReadOnlyList<IComposerSlashCommand> MatchPrefix(string prefix, int maxItems);
}
