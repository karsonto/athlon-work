using System.Text;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Prompt;

namespace Athlon.Agent.Infrastructure.Prompt;

public static class WorkspacePromptLoader
{
    private const string AgentsFileName = "AGENTS.md";
    private const string ContributingFileName = "CONTRIBUTING.md";
    private const string KnowledgeDirName = "knowledge";
    private const string KnowledgeIndexFileName = "KNOWLEDGE.md";
    private const string TruncationNotice = "\n\n... (truncated — read the full file with file_read) ...\n";

    public static void AppendWorkspaceFiles(
        StringBuilder builder,
        EnvironmentPromptContext context,
        ISshWorkspaceClient? sshClient = null)
    {
        if (!context.HasWorkspace || string.IsNullOrWhiteSpace(context.WorkspaceRoot))
        {
            return;
        }

        if (context.WorkspaceKind == WorkspaceKind.Ssh)
        {
            AppendRemoteWorkspaceFiles(builder, context, sshClient);
            return;
        }

        var workspaceRoot = Path.GetFullPath(context.WorkspaceRoot);
        var settings = context.PromptSettings;
        var hasContent = false;

        var agentsContent = TryReadAgentsMd(workspaceRoot, settings.MaxAgentsMdChars);
        if (!string.IsNullOrWhiteSpace(agentsContent))
        {
            if (!hasContent)
            {
                builder.AppendLine();
            }

            builder.AppendLine("Project rules below (from AGENTS.md) override your default habits when they conflict. Follow them for all workspace edits.");
            builder.AppendLine("## AGENTS.md");
            builder.AppendLine("<loaded_context>");
            builder.AppendLine(agentsContent.TrimEnd());
            builder.AppendLine("</loaded_context>");
            builder.AppendLine();
            hasContent = true;
        }

        var contributingContent = TryReadContributingMd(workspaceRoot, settings.MaxContributingMdChars);
        if (!string.IsNullOrWhiteSpace(contributingContent))
        {
            if (!hasContent)
            {
                builder.AppendLine();
            }

            builder.AppendLine("## CONTRIBUTING.md");
            builder.AppendLine("<loaded_context>");
            builder.AppendLine(contributingContent.TrimEnd());
            builder.AppendLine("</loaded_context>");
            builder.AppendLine();
            hasContent = true;
        }

        var knowledgeBlock = BuildKnowledgeBlock(workspaceRoot, context.IgnorePatterns, settings);
        if (!string.IsNullOrWhiteSpace(knowledgeBlock))
        {
            if (!hasContent)
            {
                builder.AppendLine();
            }

            builder.Append(knowledgeBlock);
            if (!knowledgeBlock.EndsWith('\n'))
            {
                builder.AppendLine();
            }

            builder.AppendLine();
        }
    }

    private static void AppendRemoteWorkspaceFiles(
        StringBuilder builder,
        EnvironmentPromptContext context,
        ISshWorkspaceClient? sshClient)
    {
        if (sshClient is not { IsConnected: true })
        {
            return;
        }

        var root = RemotePathNormalizer.NormalizeRoot(context.WorkspaceRoot!);
        var settings = context.PromptSettings;
        var hasContent = false;

        var agentsContent = TryReadRemoteText(sshClient, RemotePathNormalizer.Combine(root, AgentsFileName), settings.MaxAgentsMdChars);
        if (!string.IsNullOrWhiteSpace(agentsContent))
        {
            builder.AppendLine();
            builder.AppendLine("Project rules below (from AGENTS.md) override your default habits when they conflict. Follow them for all workspace edits.");
            builder.AppendLine("## AGENTS.md");
            builder.AppendLine("<loaded_context>");
            builder.AppendLine(agentsContent.TrimEnd());
            builder.AppendLine("</loaded_context>");
            builder.AppendLine();
            hasContent = true;
        }

        var contributingContent = TryReadRemoteText(
            sshClient,
            RemotePathNormalizer.Combine(root, ContributingFileName),
            settings.MaxContributingMdChars);
        if (!string.IsNullOrWhiteSpace(contributingContent))
        {
            if (!hasContent)
            {
                builder.AppendLine();
            }

            builder.AppendLine("## CONTRIBUTING.md");
            builder.AppendLine("<loaded_context>");
            builder.AppendLine(contributingContent.TrimEnd());
            builder.AppendLine("</loaded_context>");
            builder.AppendLine();
        }
    }

    private static string? TryReadRemoteText(ISshWorkspaceClient client, string remotePath, int maxChars)
    {
        try
        {
            if (!client.FileExistsAsync(remotePath).GetAwaiter().GetResult())
            {
                return null;
            }

            var text = client.ReadTextAsync(remotePath).GetAwaiter().GetResult();
            if (string.IsNullOrEmpty(text))
            {
                return text;
            }

            return text.Length <= maxChars ? text : text[..maxChars] + TruncationNotice;
        }
        catch
        {
            return null;
        }
    }

    private static string? TryReadAgentsMd(string workspaceRoot, int maxChars)
    {
        var agentsPath = Path.Combine(workspaceRoot, AgentsFileName);
        if (!File.Exists(agentsPath) || !IsUnderRoot(agentsPath, workspaceRoot))
        {
            return null;
        }

        return ReadTextWithLimit(agentsPath, maxChars);
    }

    private static string? TryReadContributingMd(string workspaceRoot, int maxChars)
    {
        var contributingPath = Path.Combine(workspaceRoot, ContributingFileName);
        if (!File.Exists(contributingPath) || !IsUnderRoot(contributingPath, workspaceRoot))
        {
            return null;
        }

        return ReadTextWithLimit(contributingPath, maxChars);
    }

    private static string? BuildKnowledgeBlock(
        string workspaceRoot,
        IReadOnlyList<string> ignorePatterns,
        PromptSettings settings)
    {
        var knowledgeRoot = Path.Combine(workspaceRoot, KnowledgeDirName);
        if (!Directory.Exists(knowledgeRoot) || !IsUnderRoot(knowledgeRoot, workspaceRoot))
        {
            return null;
        }

        var block = new StringBuilder();
        block.AppendLine("## Domain Knowledge");
        block.AppendLine("knowledge/ reference docs below; use file_read or grep_files for content not inlined.");
        block.AppendLine();

        var knowledgeMdPath = Path.Combine(knowledgeRoot, KnowledgeIndexFileName);
        if (File.Exists(knowledgeMdPath) && IsUnderRoot(knowledgeMdPath, workspaceRoot))
        {
            var indexContent = ReadTextWithLimit(knowledgeMdPath, settings.MaxKnowledgeMdChars);
            if (!string.IsNullOrWhiteSpace(indexContent))
            {
                block.AppendLine("### knowledge/KNOWLEDGE.md");
                block.AppendLine("<loaded_context>");
                block.AppendLine(indexContent.TrimEnd());
                block.AppendLine("</loaded_context>");
                block.AppendLine();
            }
        }

        var catalog = CollectKnowledgePaths(knowledgeRoot, workspaceRoot, ignorePatterns, settings.MaxKnowledgeCatalogEntries);
        if (catalog.Paths.Count > 0)
        {
            block.AppendLine("### knowledge/ file catalog");
            foreach (var path in catalog.Paths)
            {
                block.AppendLine($"- {path}");
            }

            if (catalog.Truncated)
            {
                block.AppendLine($"... ({catalog.TotalFound} paths total; listing capped at {settings.MaxKnowledgeCatalogEntries})");
            }
        }

        return block.Length <= 0 ? null : block.ToString().TrimEnd();
    }

    private static KnowledgeCatalogResult CollectKnowledgePaths(
        string knowledgeRoot,
        string workspaceRoot,
        IReadOnlyList<string> ignorePatterns,
        int maxEntries)
    {
        var ignored = ignorePatterns.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var paths = new List<string>();
        var totalFound = 0;

        foreach (var file in Directory.EnumerateFiles(knowledgeRoot, "*", SearchOption.AllDirectories))
        {
            if (!IsUnderRoot(file, workspaceRoot))
            {
                continue;
            }

            var relativeFromWorkspace = Path.GetRelativePath(workspaceRoot, file).Replace('\\', '/');
            if (ShouldIgnorePath(relativeFromWorkspace, ignored))
            {
                continue;
            }

            if (string.Equals(relativeFromWorkspace, $"{KnowledgeDirName}/{KnowledgeIndexFileName}", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            totalFound++;
            if (paths.Count < maxEntries)
            {
                paths.Add(relativeFromWorkspace);
            }
        }

        paths.Sort(StringComparer.Ordinal);
        return new KnowledgeCatalogResult(paths, totalFound, totalFound > maxEntries);
    }

    private static bool ShouldIgnorePath(string relativePath, HashSet<string> ignored)
    {
        var segments = relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Any(segment => ignored.Contains(segment));
    }

    private static string? ReadTextWithLimit(string path, int maxChars)
    {
        if (maxChars <= 0)
        {
            return null;
        }

        var content = File.ReadAllText(path, Encoding.UTF8);
        if (content.Length <= maxChars)
        {
            return content;
        }

        return content[..maxChars] + TruncationNotice;
    }

    private static bool IsUnderRoot(string fullPath, string rootPath)
    {
        var normalizedRoot = Path.GetFullPath(rootPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var normalizedPath = Path.GetFullPath(fullPath);
        return normalizedPath.Equals(normalizedRoot.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)
               || normalizedPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
    }

    private sealed record KnowledgeCatalogResult(IReadOnlyList<string> Paths, int TotalFound, bool Truncated);
}
