namespace Athlon.Agent.Core;

/// <summary>
/// AgentScope-style skill runtime: catalog filtering, progressive load, and per-turn activation.
/// </summary>
public interface ISkillRuntime : IAvailableSkillsProvider
{
    string LoadResource(string skillId, string path);

    void Activate(string skillId);

    bool IsActive(string skillId);
}
