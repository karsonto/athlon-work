using System.Text;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Prompt;

namespace Athlon.Agent.Tests;

public sealed class RuntimeContextAssemblerTests
{
    [Fact]
    public void Build_orders_contributors_and_wraps_runtime_context()
    {
        var assembler = new RuntimeContextAssembler(
        [
            new StubContributor(20, "second"),
            new StubContributor(10, "first")
        ]);

        var result = assembler.Build(CreateContext());

        Assert.NotNull(result);
        Assert.StartsWith("## Runtime context", result, StringComparison.Ordinal);
        Assert.Contains("<runtime_context>", result, StringComparison.Ordinal);
        Assert.True(result.IndexOf("first", StringComparison.Ordinal) < result.IndexOf("second", StringComparison.Ordinal));
        Assert.EndsWith("</runtime_context>", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_returns_null_when_contributors_add_no_content()
    {
        var assembler = new RuntimeContextAssembler([new StubContributor(10, string.Empty)]);

        Assert.Null(assembler.Build(CreateContext()));
    }

    private static EnvironmentPromptContext CreateContext() =>
        new()
        {
            Session = AgentSession.Create("runtime-context"),
            IgnorePatterns = [],
            Tools = [],
            SkillsDirectory = string.Empty,
            Host = new PromptTestHelpers.FakeHostEnvironment(string.Empty, string.Empty),
            PromptSettings = new PromptSettings()
        };

    private sealed class StubContributor(int priority, string content) : IRuntimeContextContributor
    {
        public int Priority { get; } = priority;

        public void Append(StringBuilder builder, EnvironmentPromptContext context)
        {
            if (!string.IsNullOrEmpty(content))
            {
                builder.AppendLine(content);
            }
        }
    }
}
