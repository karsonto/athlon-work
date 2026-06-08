using Athlon.Agent.Core;
using Athlon.Agent.Core.ComposerCommands;

namespace Athlon.Agent.Infrastructure.ComposerCommands;

public sealed class ComposerCommandExecutor(IComposerCommandRegistry registry)
{
    public async Task<ComposerCommandResult> ExecuteAsync(
        string commandName,
        string[] args,
        AgentSession session,
        AppSettings settings,
        bool isSessionBusy,
        AgentTurnCallbacks? callbacks = null,
        CancellationToken cancellationToken = default)
    {
        if (isSessionBusy)
        {
            return new ComposerCommandResult(
                ComposerCommandOutcome.Rejected,
                session,
                "当前对话正在生成，请先停止后再执行命令。");
        }

        if (!registry.TryGet(commandName, out var command) || command is null)
        {
            return new ComposerCommandResult(
                ComposerCommandOutcome.Rejected,
                session,
                $"未知命令：/{commandName}。输入 /help 查看可用命令。");
        }

        var context = new ComposerCommandContext
        {
            Session = session,
            Args = args,
            Settings = settings,
            Callbacks = callbacks,
            CancellationToken = cancellationToken
        };

        return await command.ExecuteAsync(context);
    }
}
