using System.Text.Json;
using System.Text.Json.Serialization;

namespace Athlon.Agent.Core.Teams;

/// <summary>
/// Portable Team configuration package for export / import.
/// Contains the Team definition plus every referenced Leader / Worker profile so a
/// structured JSON edit can recreate the Team elsewhere.
/// </summary>
public sealed class TeamConfigPackage
{
    public const string KindValue = "athlon.agent.team";
    public const int CurrentSchemaVersion = 1;

    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    [JsonPropertyName("kind")]
    public string Kind { get; set; } = KindValue;

    [JsonPropertyName("exportedAt")]
    public DateTimeOffset ExportedAt { get; set; } = DateTimeOffset.UtcNow;

    [JsonPropertyName("team")]
    public TeamDefinition Team { get; set; } = new();

    [JsonPropertyName("profiles")]
    public List<AgentProfile> Profiles { get; set; } = new();
}

public static class TeamConfigPackageCodec
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonFileStoreOptions.WebIndented)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    public static TeamConfigPackage Build(TeamDefinition team, IEnumerable<AgentProfile> profiles)
    {
        ArgumentNullException.ThrowIfNull(team);
        ArgumentNullException.ThrowIfNull(profiles);

        var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(team.LeaderProfileId))
        {
            referenced.Add(team.LeaderProfileId);
        }

        foreach (var workerId in team.WorkerProfileIds)
        {
            if (!string.IsNullOrWhiteSpace(workerId))
            {
                referenced.Add(workerId);
            }
        }

        var packageProfiles = profiles
            .Where(p => !string.IsNullOrWhiteSpace(p.Id) && referenced.Contains(p.Id))
            .GroupBy(p => p.Id, StringComparer.OrdinalIgnoreCase)
            .Select(g => CloneProfile(g.First()))
            .OrderBy(p => string.Equals(p.Id, team.LeaderProfileId, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new TeamConfigPackage
        {
            SchemaVersion = TeamConfigPackage.CurrentSchemaVersion,
            Kind = TeamConfigPackage.KindValue,
            ExportedAt = DateTimeOffset.UtcNow,
            Team = CloneTeam(team),
            Profiles = packageProfiles
        };
    }

    public static string Serialize(TeamConfigPackage package) =>
        JsonSerializer.Serialize(package, JsonOptions);

    public static TeamConfigPackage Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new InvalidOperationException("Team JSON 为空。");
        }

        TeamConfigPackage? package;
        try
        {
            package = JsonSerializer.Deserialize<TeamConfigPackage>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("Team JSON 无法解析：" + ex.Message, ex);
        }

        if (package is null)
        {
            throw new InvalidOperationException("Team JSON 为空对象。");
        }

        Validate(package);
        return package;
    }

    public static void Validate(TeamConfigPackage package)
    {
        ArgumentNullException.ThrowIfNull(package);

        if (!string.Equals(package.Kind, TeamConfigPackage.KindValue, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"不支持的 kind：{package.Kind}（期望 {TeamConfigPackage.KindValue}）。");
        }

        if (package.SchemaVersion < 1 || package.SchemaVersion > TeamConfigPackage.CurrentSchemaVersion)
        {
            throw new InvalidOperationException(
                $"不支持的 schemaVersion：{package.SchemaVersion}（当前支持 1..{TeamConfigPackage.CurrentSchemaVersion}）。");
        }

        if (package.Team is null || string.IsNullOrWhiteSpace(package.Team.Name))
        {
            throw new InvalidOperationException("team.name 不能为空。");
        }

        if (package.Profiles is null || package.Profiles.Count == 0)
        {
            throw new InvalidOperationException("profiles 不能为空。");
        }

        var byId = new Dictionary<string, AgentProfile>(StringComparer.OrdinalIgnoreCase);
        foreach (var profile in package.Profiles)
        {
            if (profile is null || string.IsNullOrWhiteSpace(profile.Id))
            {
                throw new InvalidOperationException("profiles[].id 不能为空。");
            }

            if (string.IsNullOrWhiteSpace(profile.Name))
            {
                throw new InvalidOperationException($"profile {profile.Id} 缺少 name。");
            }

            if (!byId.TryAdd(profile.Id, profile))
            {
                throw new InvalidOperationException($"重复的 profile id：{profile.Id}。");
            }
        }

        if (string.IsNullOrWhiteSpace(package.Team.LeaderProfileId)
            || !byId.ContainsKey(package.Team.LeaderProfileId))
        {
            throw new InvalidOperationException("team.leaderProfileId 必须指向 profiles 中的一项。");
        }

        foreach (var workerId in package.Team.WorkerProfileIds ?? [])
        {
            if (string.IsNullOrWhiteSpace(workerId))
            {
                continue;
            }

            if (!byId.ContainsKey(workerId))
            {
                throw new InvalidOperationException($"workerProfileId {workerId} 不在 profiles 中。");
            }
        }
    }

    /// <summary>
    /// Remaps team / profile ids so the package can be imported alongside existing Teams
    /// without collisions. Caller persists the returned team + profiles.
    /// </summary>
    public static (TeamDefinition Team, IReadOnlyList<AgentProfile> Profiles) MaterializeForImport(
        TeamConfigPackage package,
        bool appendImportedSuffix = true)
    {
        Validate(package);

        var idMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var profile in package.Profiles)
        {
            idMap[profile.Id] = "profile-" + Guid.NewGuid().ToString("N")[..10];
        }

        var profiles = new List<AgentProfile>(package.Profiles.Count);
        foreach (var source in package.Profiles)
        {
            var clone = CloneProfile(source);
            clone.Id = idMap[source.Id];
            profiles.Add(clone);
        }

        var team = CloneTeam(package.Team);
        team.Id = "team-" + Guid.NewGuid().ToString("N")[..10];
        team.LeaderProfileId = idMap[package.Team.LeaderProfileId];
        team.WorkerProfileIds = (package.Team.WorkerProfileIds ?? [])
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => idMap[id])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        team.CreatedAt = DateTimeOffset.UtcNow;
        team.UpdatedAt = team.CreatedAt;
        if (appendImportedSuffix
            && !team.Name.EndsWith(" (imported)", StringComparison.OrdinalIgnoreCase))
        {
            team.Name = team.Name.TrimEnd() + " (imported)";
        }

        return (team, profiles);
    }

    private static TeamDefinition CloneTeam(TeamDefinition team) =>
        JsonSerializer.Deserialize<TeamDefinition>(
            JsonSerializer.Serialize(team, JsonOptions),
            JsonOptions)!;

    private static AgentProfile CloneProfile(AgentProfile profile) =>
        JsonSerializer.Deserialize<AgentProfile>(
            JsonSerializer.Serialize(profile, JsonOptions),
            JsonOptions)!;
}
