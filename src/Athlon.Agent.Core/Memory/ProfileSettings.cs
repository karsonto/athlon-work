namespace Athlon.Agent.Core.Memory;

/// <summary>
/// Account-level habit profile settings. Independent of <see cref="MemorySettings"/>, which stays
/// workspace + session scoped.
/// </summary>
public sealed class ProfileSettings
{
    /// <summary>Master switch. When false neither flush nor tools touch the profile.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Storage backend. Only Local is implemented today.</summary>
    public ProfileBackendKind Backend { get; set; } = ProfileBackendKind.Local;

    /// <summary>Local backend directory name under the app data root (~/.athlon-agent).</summary>
    public string DirName { get; set; } = "profile";

    public int UserMaxChars { get; set; } = 3000;

    public int LessonsMaxChars { get; set; } = 2500;

    /// <summary>How long a USER.md refresh stays fresh. Default 2 hours.</summary>
    public TimeSpan UserRefreshCooldown { get; set; } = TimeSpan.FromHours(2);

    public int MaxChars(ProfileKind kind) => kind switch
    {
        ProfileKind.User => UserMaxChars,
        ProfileKind.Lessons => LessonsMaxChars,
        _ => UserMaxChars
    };
}
