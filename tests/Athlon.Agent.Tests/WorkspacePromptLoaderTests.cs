using System.Text;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Prompt;
using Athlon.Agent.Infrastructure.Prompt;

namespace Athlon.Agent.Tests;

public sealed class WorkspacePromptLoaderTests
{
    [Fact]
    public void AppendWorkspaceFiles_InjectsAgentsMd_WhenPresent()
    {
        var root = CreateWorkspaceRoot();
        File.WriteAllText(Path.Combine(root, "AGENTS.md"), "# Agent Rules\nAlways run tests.");

        try
        {
            var builder = new StringBuilder();
            WorkspacePromptLoader.AppendWorkspaceFiles(builder, CreateContext(root));

            var text = builder.ToString();
            Assert.Contains("## AGENTS.md", text, StringComparison.Ordinal);
            Assert.Contains("<loaded_context>", text, StringComparison.Ordinal);
            Assert.Contains("# Agent Rules", text, StringComparison.Ordinal);
            Assert.DoesNotContain("Honor AGENTS.md", text, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void AppendWorkspaceFiles_TruncatesLargeAgentsMd()
    {
        var root = CreateWorkspaceRoot();
        File.WriteAllText(Path.Combine(root, "AGENTS.md"), new string('A', 9000));

        try
        {
            var builder = new StringBuilder();
            WorkspacePromptLoader.AppendWorkspaceFiles(
                builder,
                CreateContext(root, new PromptSettings { MaxAgentsMdChars = 100 }));

            Assert.Contains("truncated", builder.ToString(), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void AppendWorkspaceFiles_ListsKnowledgeCatalog_AndSkipsIgnoredDirs()
    {
        var root = CreateWorkspaceRoot();
        var knowledgeDir = Path.Combine(root, "knowledge", "refs");
        Directory.CreateDirectory(knowledgeDir);
        File.WriteAllText(Path.Combine(knowledgeDir, "guide.md"), "# Guide");
        Directory.CreateDirectory(Path.Combine(root, "knowledge", "bin"));
        File.WriteAllText(Path.Combine(root, "knowledge", "bin", "skip.dll"), "x");

        try
        {
            var builder = new StringBuilder();
            WorkspacePromptLoader.AppendWorkspaceFiles(
                builder,
                CreateContext(root, ignorePatterns: [".git", "bin", "obj", "node_modules"]));

            var text = builder.ToString();
            Assert.Contains("## Domain Knowledge", text, StringComparison.Ordinal);
            Assert.Contains("knowledge/refs/guide.md", text, StringComparison.Ordinal);
            Assert.DoesNotContain("knowledge/bin/", text, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void AppendWorkspaceFiles_OmitsBlock_WhenNoWorkspaceFiles()
    {
        var root = CreateWorkspaceRoot();
        try
        {
            var builder = new StringBuilder();
            WorkspacePromptLoader.AppendWorkspaceFiles(builder, CreateContext(root));
            Assert.Equal(string.Empty, builder.ToString());
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static string CreateWorkspaceRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "athlon-workspace-prompt", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static EnvironmentPromptContext CreateContext(
        string workspaceRoot,
        PromptSettings? promptSettings = null,
        IReadOnlyList<string>? ignorePatterns = null) =>
        new()
        {
            Session = AgentSession.Create("ws-test"),
            WorkspaceRoot = workspaceRoot,
            WorkspaceName = "test",
            IgnorePatterns = ignorePatterns ?? [".git", "bin", "obj", "node_modules"],
            Tools = Array.Empty<ToolDefinition>(),
            SkillsDirectory = @"C:\Users\test\.athlon-agent\skills",
            Host = new PromptTestHelpers.FakeHostEnvironment(@"C:\Users\test\.athlon-agent\skills", @"C:\Users\test\.athlon-agent"),
            PromptSettings = promptSettings ?? new PromptSettings()
        };
}
