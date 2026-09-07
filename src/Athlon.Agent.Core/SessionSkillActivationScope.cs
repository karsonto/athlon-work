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
                LastActivatedSkillId = skillId;
            }
        }

        public bool IsActive(string skillId) =>
            !string.IsNullOrWhiteSpace(skillId) && _activeSkillIds.Contains(skillId);

        public IReadOnlyCollection<string> ActiveSkillIds => _activeSkillIds;

        /// <summary>Most recently activated skill id in this turn (usage attribution target).</summary>
        public string? LastActivatedSkillId { get; private set; }

        public int TotalToolCalls { get; private set; }

        public int SucceededToolCalls { get; private set; }

        public int FailedToolCalls { get; private set; }

        /// <summary>
        /// Counts one executed tool outcome for the current turn. Used to report skill
        /// usage success/failure at turn end (see <c>skill_usage</c> behavior event).
        /// </summary>
        public void RecordToolOutcome(bool succeeded)
        {
            TotalToolCalls++;
            if (succeeded)
            {
                SucceededToolCalls++;
            }
            else
            {
                FailedToolCalls++;
            }
        }
    }
}
