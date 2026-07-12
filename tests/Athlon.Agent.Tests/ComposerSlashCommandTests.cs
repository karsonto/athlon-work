using Athlon.Agent.App.Services;
using Athlon.Agent.App.Services.SlashCommands;
using Athlon.Agent.Core;

namespace Athlon.Agent.Tests;

public sealed class ComposerSlashCommandTests
{
    [Fact]
    public void Registry_TryGetExact_and_MatchPrefix()
    {
        var registry = ComposerTestFactory.CreateSlashRegistry(new StubSlashCommand("compact", "Compact context"));
        Assert.True(registry.TryGetExact("compact", out var command));
        Assert.Equal("compact", command!.Name);
        Assert.Single(registry.MatchPrefix("comp", 5));
    }

    [Fact]
    public void Executor_TryParseExactCommand()
    {
        var registry = ComposerTestFactory.CreateSlashRegistry(new StubSlashCommand("compact", "Compact context"));
        var executor = ComposerTestFactory.CreateSlashExecutor(registry);
        Assert.True(executor.TryParseExactCommand("/compact", out var command));
        Assert.Equal("compact", command!.Name);
        Assert.False(executor.TryParseExactCommand("/compact extra", out _));
        Assert.False(executor.TryParseExactCommand("//skill:demo", out _));
    }

    [Fact]
    public void Executor_LooksLikeUnregisteredExactCommand()
    {
        var executor = ComposerTestFactory.CreateSlashExecutor();
        Assert.True(executor.LooksLikeUnregisteredExactCommand("/compact"));
        Assert.False(executor.LooksLikeUnregisteredExactCommand("/compact extra"));
    }

    private sealed class StubSlashCommand(string name, string description) : IComposerSlashCommand
    {
        public string Name { get; } = name;
        public string Description { get; } = description;

        public bool IsAvailable(ComposerSlashCommandContext context) => true;

        public ValueTask<ComposerSlashCommandResult> ExecuteAsync(
            ComposerSlashCommandContext context,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new ComposerSlashCommandResult(true, "ok"));
    }
}
