namespace Athlon.Agent.Core.Debug;

public sealed record DebugHypothesis(string Id, string Summary, DebugHypothesisStatus Status = DebugHypothesisStatus.Open);
