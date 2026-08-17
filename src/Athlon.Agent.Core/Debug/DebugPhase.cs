namespace Athlon.Agent.Core.Debug;

public enum DebugPhase
{
    Hypothesize,
    Instrument,
    AwaitRepro,
    Analyze,
    Fix,
    AwaitVerify,
    Cleanup,
    Done
}
