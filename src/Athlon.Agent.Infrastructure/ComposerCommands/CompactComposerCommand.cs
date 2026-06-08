using Athlon.Agent.Core.ComposerCommands;

namespace Athlon.Agent.Infrastructure.ComposerCommands;

public sealed class CompactComposerCommand(ISessionCompactionService compactionService) : IComposerCommand
{
    public ComposerCommandDescriptor Descriptor { get; } = new(
        "compact",
        "手动压缩当前对话上下文（类似 Claude Code /compact）。",
        "/compact");

    public async Task<ComposerCommandResult> ExecuteAsync(ComposerCommandContext context)
    {
        var result = await compactionService.CompactManuallyAsync(
            context.Session,
            context.Callbacks,
            context.CancellationToken);

        return new ComposerCommandResult(
            ComposerCommandOutcome.Handled,
            result.Session,
            result.StatusMessage);
    }
}
