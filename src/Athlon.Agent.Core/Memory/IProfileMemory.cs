namespace Athlon.Agent.Core.Memory;

/// <summary>Which profile document is being addressed.</summary>
public enum ProfileKind
{
    User,
    Lessons
}

/// <summary>Storage backend. Remote is reserved for a later phase; not selectable yet.</summary>
public enum ProfileBackendKind
{
    Local,
    Remote
}

public sealed record ProfileDocument(
    ProfileKind Kind,
    string Content,
    DateTimeOffset? UpdatedAt,
    ProfileBackendKind Backend)
{
    public bool IsEmpty => string.IsNullOrWhiteSpace(Content);
}

public sealed record ProfileCapabilities(
    ProfileBackendKind Backend,
    bool CanWrite,
    bool CanDelete,
    bool IsRemote);

/// <summary>
/// Account-level habit profile. One profile per user, shared by every workspace and session.
/// Implementations may be local files today and a remote account service later; callers must
/// not assume synchronous, local, or writable storage.
/// </summary>
public interface IProfileMemory
{
    ProfileCapabilities Capabilities { get; }

    /// <summary>Returns null when the document has never been written.</summary>
    Task<ProfileDocument?> ReadAsync(ProfileKind kind, CancellationToken cancellationToken = default);

    /// <summary>Full replacement of one document. No-op when <see cref="ProfileCapabilities.CanWrite"/> is false.</summary>
    Task WriteAsync(ProfileKind kind, string content, CancellationToken cancellationToken = default);

    /// <summary>Returns true when a document existed and was removed.</summary>
    Task<bool> DeleteAsync(ProfileKind kind, CancellationToken cancellationToken = default);
}

/// <summary>Maps <see cref="ProfileKind"/> to its canonical file name and prompt heading.</summary>
public static class ProfileKinds
{
    public const string UserFileName = "USER.md";
    public const string LessonsFileName = "LESSONS.md";

    public static string FileName(ProfileKind kind) => kind switch
    {
        ProfileKind.User => UserFileName,
        ProfileKind.Lessons => LessonsFileName,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown profile kind.")
    };

    public static string SectionTitle(ProfileKind kind) => kind switch
    {
        ProfileKind.User => "USER",
        ProfileKind.Lessons => "LESSONS",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown profile kind.")
    };

    public static IReadOnlyList<ProfileKind> All { get; } = [ProfileKind.User, ProfileKind.Lessons];
}
