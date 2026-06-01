using System.IO;
using Athlon.Agent.App.ViewModels;

namespace Athlon.Agent.App.Services;

public static class WorkspaceFileIconResolver
{
    public static WorkspaceFileIconKind Resolve(string? name, string? fullPath, bool isDirectory, bool isPlaceholder)
    {
        if (isPlaceholder)
        {
            return WorkspaceFileIconKind.Placeholder;
        }

        if (isDirectory)
        {
            return WorkspaceFileIconKind.Folder;
        }

        var fileName = name ?? Path.GetFileName(fullPath ?? string.Empty);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return WorkspaceFileIconKind.File;
        }

        if (fileName.Equals(".gitignore", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals(".gitattributes", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals(".gitmodules", StringComparison.OrdinalIgnoreCase))
        {
            return WorkspaceFileIconKind.Git;
        }

        if (fileName.Equals("Directory.Build.props", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("Directory.Build.targets", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("Directory.Packages.props", StringComparison.OrdinalIgnoreCase))
        {
            return WorkspaceFileIconKind.MsBuild;
        }

        if (fileName.Equals("Dockerfile", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith(".dockerfile", StringComparison.OrdinalIgnoreCase))
        {
            return WorkspaceFileIconKind.Docker;
        }

        var extension = Path.GetExtension(fileName);
        if (string.IsNullOrEmpty(extension) && fileName.StartsWith("README", StringComparison.OrdinalIgnoreCase))
        {
            return WorkspaceFileIconKind.Markdown;
        }

        return extension.ToLowerInvariant() switch
        {
            ".cs" => WorkspaceFileIconKind.CSharp,
            ".csproj" => WorkspaceFileIconKind.Project,
            ".sln" or ".slnx" => WorkspaceFileIconKind.Solution,
            ".md" or ".markdown" => WorkspaceFileIconKind.Markdown,
            ".json" or ".jsonc" => WorkspaceFileIconKind.Json,
            ".xml" or ".xaml" or ".axaml" or ".cshtml" or ".razor" or ".resx" or ".props" or ".targets" => WorkspaceFileIconKind.Xml,
            ".html" or ".htm" => WorkspaceFileIconKind.Html,
            ".css" or ".scss" or ".less" => WorkspaceFileIconKind.Css,
            ".js" or ".mjs" or ".cjs" or ".jsx" => WorkspaceFileIconKind.JavaScript,
            ".ts" or ".tsx" => WorkspaceFileIconKind.TypeScript,
            ".py" or ".pyw" => WorkspaceFileIconKind.Python,
            ".sh" or ".bash" or ".zsh" => WorkspaceFileIconKind.Shell,
            ".ps1" or ".psm1" or ".psd1" => WorkspaceFileIconKind.PowerShell,
            ".bat" or ".cmd" => WorkspaceFileIconKind.Shell,
            ".yml" or ".yaml" => WorkspaceFileIconKind.Yaml,
            ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".svg" or ".ico" => WorkspaceFileIconKind.Image,
            ".config" or ".ini" or ".cfg" or ".conf" or ".toml" => WorkspaceFileIconKind.Config,
            ".gitignore" => WorkspaceFileIconKind.Git,
            _ => WorkspaceFileIconKind.File,
        };
    }
}
