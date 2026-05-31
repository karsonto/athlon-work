namespace Athlon.Agent.Core;

/// <summary>
/// Per-async-flow activated skill ids for the current agent turn.
/// </summary>
public sealed class SessionSkillActivationScope : IDisposable
{
    private static readonly AsyncLocal<SessionSkillActivationState?> Current = new();

    private readonly SessionSkillActivationState? _previous;

    private SessionSkillActivationScope(bool clearOnEnter)
    {
        _previous = Current.Value;
        Current.Value = clearOnEnter
            ? new SessionSkillActivationState()
            : _previous ?? new SessionSkillActivationState();
    }

    public static SessionSkillActivationState? CurrentState => Current.Value;

    /// <summary>Starts a fresh activation set for a new user turn.</summary>
    public static IDisposable EnterNewTurn() => new SessionSkillActivationScope(clearOnEnter: true);

    public void Dispose() => Current.Value = _previous;

    public sealed class SessionSkillActivationState
    {
        private readonly HashSet<string> _activeSkillIds = new(StringComparer.Ordinal);

        public void Activate(string skillId)
        {
            if (!string.IsNullOrWhiteSpace(skillId))
            {
                _activeSkillIds.Add(skillId);
            }
        }

        public bool IsActive(string skillId) =>
            !string.IsNullOrWhiteSpace(skillId) && _activeSkillIds.Contains(skillId);

        public IReadOnlyCollection<string> ActiveSkillIds => _activeSkillIds;
    }
}
