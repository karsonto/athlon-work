namespace Athlon.Agent.Core.Debug;

public enum DebugPhase
{
    Hypothesize = 0,
    Instrument = 1,
    AwaitRepro = 2,
    Analyze = 3,
    Fix = 4,
    AwaitVerify = 5,
    Cleanup = 6,
    Done = 7,
    AwaitFixConfirm = 8
}

public static class DebugPhaseRules
{
    public static bool IsAwaitingUser(this DebugPhase phase) =>
        phase is DebugPhase.AwaitRepro or DebugPhase.AwaitFixConfirm or DebugPhase.AwaitVerify;

    public static bool BlocksMcp(this DebugPhase phase) =>
        phase is DebugPhase.Hypothesize
            or DebugPhase.Analyze
            or DebugPhase.AwaitRepro
            or DebugPhase.AwaitFixConfirm
            or DebugPhase.AwaitVerify
            or DebugPhase.Done;

    public static bool IsReadOnlyFollowUp(this DebugPhase phase) =>
        phase is DebugPhase.AwaitRepro or DebugPhase.AwaitFixConfirm or DebugPhase.AwaitVerify;
}
