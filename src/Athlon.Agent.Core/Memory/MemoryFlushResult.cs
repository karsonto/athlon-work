namespace Athlon.Agent.Core.Memory;

public sealed record MemoryFlushResult(
    bool Flushed,
    string? Summary = null,
    string? Error = null)
{
    public static MemoryFlushResult Skipped => new(false);
    public static MemoryFlushResult Success(string summary) => new(true, summary);
    public static MemoryFlushResult Failed(string error) => new(false, null, error);
}
