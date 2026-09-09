namespace Athlon.Agent.Core;

/// <summary>
/// Centralizes identifier generation. Project-wide callers previously scattered
/// <c>Guid.NewGuid().ToString("N")</c> (20+ sites) across Core/Infrastructure/App.
/// Using <see cref="NewId"/> and <see cref="EnsureId(string?)"/> keeps the ID format
/// and the "generate-if-missing" semantics in one place.
/// </summary>
public static class IdGen
{
    /// <summary>Generates a compact, lowercase, hyphen-free 32-char ID.</summary>
    public static string NewId() => Guid.NewGuid().ToString("N");

    /// <summary>
    /// Returns <paramref name="current"/> if it is non-empty; otherwise generates a new ID.
    /// Useful for "assign an ID unless one already exists" call sites.
    /// </summary>
    public static string EnsureId(string? current) =>
        string.IsNullOrWhiteSpace(current) ? NewId() : current;
}
