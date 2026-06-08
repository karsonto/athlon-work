using System.Text;
using Athlon.Agent.Core.ComposerCommands;
using Microsoft.Extensions.DependencyInjection;

namespace Athlon.Agent.Infrastructure.ComposerCommands;

public sealed class HelpComposerCommand(IServiceProvider services) : IComposerCommand
{
    public ComposerCommandDescriptor Descriptor { get; } = new(
        "help",
        "列出可用的斜杠命令。",
        "/help [command]");

    public Task<ComposerCommandResult> ExecuteAsync(ComposerCommandContext context)
    {
        var registry = services.GetRequiredService<IComposerCommandRegistry>();
        string status;
        if (context.Args.Length > 0
            && registry.TryGet(context.Args[0], out var command)
            && command is not null)
        {
            var descriptor = command.Descriptor;
            status = $"{descriptor.Usage} — {descriptor.Description}";
        }
        else if (context.Args.Length > 0)
        {
            status = $"未知命令：{context.Args[0]}。输入 /help 查看全部命令。";
        }
        else
        {
            var builder = new StringBuilder();
            builder.AppendLine("可用斜杠命令：");
            foreach (var descriptor in registry.List())
            {
                builder.Append("  ");
                builder.Append(descriptor.Usage);
                builder.Append(" — ");
                builder.AppendLine(descriptor.Description);
            }

            status = builder.ToString().TrimEnd();
        }

        return Task.FromResult(new ComposerCommandResult(
            ComposerCommandOutcome.Handled,
            context.Session,
            status));
    }
}
