using System.Text;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Harness;
using Athlon.Agent.Core.Memory;
using Athlon.Agent.Core.Prompt;
using Athlon.Agent.Infrastructure.Memory;

namespace Athlon.Agent.Tests;

public sealed class MemoryPromptContributorTests
{
    [Fact]
    public void Append_NoneMode_DoesNotInjectMemory()
    {
        var settings = new AppSettings { Memory = { InlinePromptMode = MemoryInlinePromptMode.None } };
        var contributor = CreateContributor("remember this", harnessEnabled: true, settings);
        var builder = new StringBuilder();

        contributor.Append(builder, CreateContext());

        Assert.Equal(string.Empty, builder.ToString());
    }

    [Fact]
    public void Append_PreviewMode_InjectsTruncatedPreview()
    {
        var settings = new AppSettings
        {
            Memory =
            {
                InlinePromptMode = MemoryInlinePromptMode.Preview,
                MaxInlineMemoryChars = 20
            }
        };
        var contributor = CreateContributor(new string('x', 100), harnessEnabled: true, settings);
        var builder = new StringBuilder();

        contributor.Append(builder, CreateContext());

        var text = builder.ToString();
        Assert.Contains("## Long-Term Memory", text, StringComparison.Ordinal);
        Assert.Contains("memory_search / memory_get", text, StringComparison.Ordinal);
        Assert.Contains(new string('x', 20), text, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('x', 21), text, StringComparison.Ordinal);
    }

    [Fact]
    public void Append_FullMode_InjectsFullMemory()
    {
        var settings = new AppSettings { Memory = { InlinePromptMode = MemoryInlinePromptMode.Full } };
        var content = "User prefers tabs over spaces.";
        var contributor = CreateContributor(content, harnessEnabled: true, settings);
        var builder = new StringBuilder();

        contributor.Append(builder, CreateContext());

        var text = builder.ToString();
        Assert.Contains("<long_term_memory>", text, StringComparison.Ordinal);
        Assert.Contains(content, text, StringComparison.Ordinal);
    }

    [Fact]
    public void Append_Skips_WhenHarnessDisabled()
    {
        var settings = new AppSettings { Memory = { InlinePromptMode = MemoryInlinePromptMode.Full } };
        var contributor = CreateContributor("remember this", harnessEnabled: false, settings);
        var builder = new StringBuilder();

        contributor.Append(builder, CreateContext());

        Assert.DoesNotContain("Long-Term Memory", builder.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveInlineContent_FullMode_TruncatesWithNotice()
    {
        var content = new string('a', 100);
        var result = MemoryPromptContributor.ResolveInlineContent(content, MemoryInlinePromptMode.Full, maxFullChars: 50);

        Assert.StartsWith(new string('a', 50), result, StringComparison.Ordinal);
        Assert.Contains("memory_get", result, StringComparison.Ordinal);
    }

    [Fact]
    public void EnforceMaxLength_TruncatesWhenOverLimit()
    {
        var content = new string('b', 100);
        var result = MemoryConsolidationService.EnforceMaxLength(content, 40);

        Assert.StartsWith(new string('b', 40), result, StringComparison.Ordinal);
        Assert.Contains("...(truncated", result, StringComparison.Ordinal);
        Assert.Contains("memory_get", result, StringComparison.Ordinal);
    }

    private static MemoryPromptContributor CreateContributor(string memoryContent, bool harnessEnabled, AppSettings settings)
    {
        var harness = RouterTestDependencies.CreateSessionHarnessState(enabled: harnessEnabled);
        var accessor = RouterTestDependencies.CreateRunContextAccessor(harnessEnabled: harnessEnabled);
        var memory = new StubLongTermMemory(memoryContent);
        return new MemoryPromptContributor(memory, harness, accessor, settings);
    }

    private static EnvironmentPromptContext CreateContext() =>
        new()
        {
            Session = AgentSession.Create("memory-prompt-test"),
            WorkspaceRoot = @"C:\work\demo",
            Tools = Array.Empty<ToolDefinition>(),
            SkillsDirectory = @"C:\Users\test\.athlon-agent\skills",
            Host = new PromptTestHelpers.FakeHostEnvironment(@"C:\Users\test\.athlon-agent\skills", @"C:\Users\test\.athlon-agent"),
            PromptSettings = new PromptSettings()
        };

    private sealed class StubLongTermMemory(string curated) : ILongTermMemory
    {
        public Task<string> ReadCuratedAsync(CancellationToken cancellationToken = default) => Task.FromResult(curated);
        public Task<string> ReadDailyAsync(DateTime date, CancellationToken cancellationToken = default) => Task.FromResult("");
        public Task<string> ReadDailyFileAsync(string fileName, CancellationToken cancellationToken = default) => Task.FromResult("");
        public Task AppendDailyAsync(string text, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task WriteCuratedAsync(string content, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<DateTime> ReadWatermarkAsync(CancellationToken cancellationToken = default) => Task.FromResult(DateTime.MinValue);
        public Task WriteWatermarkAsync(DateTime watermark, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<string>> ListDailyFilesAfterAsync(DateTime after, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
        public Task<IReadOnlyList<string>> ListAllMemoryFilePathsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>(["MEMORY.md"]);
        public Task ArchiveDailyFileAsync(string relativePath, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
