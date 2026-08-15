namespace Athlon.Agent.Core.Prompt;

/// <summary>
/// Conventional order bands for <see cref="IEnvironmentPromptSection.Order"/>.
/// Distinct bands keep identity → mode → host → workspace → workflow → tool guidance stable for diagnostics and cache shape.
/// </summary>
public static class PromptSectionBands
{
    public const int Identity = 0;
    public const int SubAgentPersona = 50;
    public const int Mode = 100;
    public const int Host = 200;
    public const int Encoding = 210;
    public const int Workspace = 300;
    public const int WorkflowStart = 400;
    public const int ToolGuidanceStart = 450;
    public const int Skills = 600;
    public const int Product = 700;
}
