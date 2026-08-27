namespace Athlon.Agent.Infrastructure.SkillHub;

public sealed class RemoteSkillDto
{
    public string Id { get; set; } = "";
    public string EnglishName { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Category { get; set; } = "";
    public string Position { get; set; } = "";
    public long PackageSize { get; set; }
    public string PackageSha256 { get; set; } = "";
    public string Download { get; set; } = "";
}

public sealed class RemoteSkillListResponse
{
    public List<RemoteSkillDto> Items { get; set; } = [];
}
