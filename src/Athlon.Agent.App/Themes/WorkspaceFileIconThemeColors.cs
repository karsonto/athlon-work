using System.Windows.Media;
using Athlon.Agent.App.ViewModels;

namespace Athlon.Agent.App.Themes;

/// <summary>Workspace tree file-type icon accent colors.</summary>
public sealed class WorkspaceFileIconThemeColors
{
    public required Color Placeholder { get; init; }
    public required Color Folder { get; init; }
    public required Color File { get; init; }
    public required Color CSharp { get; init; }
    public required Color Project { get; init; }
    public required Color Solution { get; init; }
    public required Color Markdown { get; init; }
    public required Color Json { get; init; }
    public required Color Xml { get; init; }
    public required Color Html { get; init; }
    public required Color Css { get; init; }
    public required Color JavaScript { get; init; }
    public required Color TypeScript { get; init; }
    public required Color Python { get; init; }
    public required Color Shell { get; init; }
    public required Color Git { get; init; }
    public required Color Yaml { get; init; }
    public required Color Docker { get; init; }
    public required Color Image { get; init; }
    public required Color MsBuild { get; init; }
    public required Color Config { get; init; }

    public Color Resolve(WorkspaceFileIconKind kind) => kind switch
    {
        WorkspaceFileIconKind.Placeholder => Placeholder,
        WorkspaceFileIconKind.Folder => Folder,
        WorkspaceFileIconKind.CSharp => CSharp,
        WorkspaceFileIconKind.Project => Project,
        WorkspaceFileIconKind.Solution => Solution,
        WorkspaceFileIconKind.Markdown => Markdown,
        WorkspaceFileIconKind.Json => Json,
        WorkspaceFileIconKind.Xml => Xml,
        WorkspaceFileIconKind.Html => Html,
        WorkspaceFileIconKind.Css => Css,
        WorkspaceFileIconKind.JavaScript => JavaScript,
        WorkspaceFileIconKind.TypeScript => TypeScript,
        WorkspaceFileIconKind.Python => Python,
        WorkspaceFileIconKind.Shell => Shell,
        WorkspaceFileIconKind.PowerShell => Shell,
        WorkspaceFileIconKind.Git => Git,
        WorkspaceFileIconKind.Yaml => Yaml,
        WorkspaceFileIconKind.Docker => Docker,
        WorkspaceFileIconKind.Image => Image,
        WorkspaceFileIconKind.MsBuild => MsBuild,
        WorkspaceFileIconKind.Config => Config,
        _ => File,
    };
}
