using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using Athlon.Agent.Core.Teams;
using Athlon.Agent.Infrastructure;

namespace Athlon.Agent.App.Services.Room;

public sealed class RoomAgentProfileStore
{
    public static readonly string[] CatalogTools =
    [
        "file_list",
        "file_read",
        "file_write",
        "file_edit",
        "apply_patch",
        "grep_files",
        "glob_files",
        "execute_command",
        "browser_navigate",
        "room_share_file"
    ];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly string _roomsRoot;
    private readonly string _legacyGlobalPath;
    private readonly object _gate = new();
    private readonly ConcurrentDictionary<string, AgentProfile> _cache = new(StringComparer.OrdinalIgnoreCase);
    private AgentProfile? _legacyTemplate;

    public RoomAgentProfileStore(IAppPathProvider paths)
    {
        Directory.CreateDirectory(paths.ConfigPath);
        _roomsRoot = Path.Combine(paths.RootPath, "rooms");
        Directory.CreateDirectory(_roomsRoot);
        _legacyGlobalPath = Path.Combine(paths.ConfigPath, "room-agent-profile.json");
    }

    public AgentProfile GetOrCreate(string roomId)
    {
        if (!RoomStore.IsSafeId(roomId))
        {
            throw new ArgumentException("Invalid room id.", nameof(roomId));
        }

        lock (_gate)
        {
            if (_cache.TryGetValue(roomId, out var cached))
            {
                return Clone(cached);
            }

            var path = GetProfilePath(roomId);
            if (File.Exists(path))
            {
                try
                {
                    var loaded = JsonSerializer.Deserialize<AgentProfile>(File.ReadAllText(path), JsonOptions);
                    if (loaded is not null)
                    {
                        Normalize(loaded);
                        _cache[roomId] = loaded;
                        return Clone(loaded);
                    }
                }
                catch
                {
                    // fall through to default
                }
            }

            var created = Clone(GetLegacyTemplateOrDefault());
            PersistUnlocked(roomId, created);
            _cache[roomId] = created;
            return Clone(created);
        }
    }

    public void Save(string roomId, AgentProfile profile)
    {
        if (!RoomStore.IsSafeId(roomId))
        {
            throw new ArgumentException("Invalid room id.", nameof(roomId));
        }

        ArgumentNullException.ThrowIfNull(profile);
        Normalize(profile);
        lock (_gate)
        {
            PersistUnlocked(roomId, profile);
            _cache[roomId] = Clone(profile);
        }
    }

    private string GetProfilePath(string roomId) =>
        Path.Combine(_roomsRoot, roomId, "agent-profile.json");

    private void PersistUnlocked(string roomId, AgentProfile profile)
    {
        var path = GetProfilePath(roomId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(profile, JsonOptions));
    }

    private AgentProfile GetLegacyTemplateOrDefault()
    {
        if (_legacyTemplate is not null)
        {
            return _legacyTemplate;
        }

        if (File.Exists(_legacyGlobalPath))
        {
            try
            {
                var loaded = JsonSerializer.Deserialize<AgentProfile>(
                    File.ReadAllText(_legacyGlobalPath),
                    JsonOptions);
                if (loaded is not null)
                {
                    Normalize(loaded);
                    _legacyTemplate = loaded;
                    return loaded;
                }
            }
            catch
            {
                // fall through
            }
        }

        _legacyTemplate = CreateDefault();
        return _legacyTemplate;
    }

    private static AgentProfile CreateDefault()
    {
        var profile = new AgentProfile
        {
            Id = RoomAgentIds.ProfileId,
            Name = "Room Agent",
            Description = "LAN team room agent",
            AgentMd =
                "You are Athlon in a shared LAN team room. Reply concisely like a teammate. "
                + "When the user asks you to create or edit a file and share it with the room, "
                + "edit it in the workspace then call room_share_file with the file path.",
            ToolAllowList = CatalogTools.Where(t => t != "room_share_file").Take(5).Append("room_share_file").ToList(),
            SkillNames = new List<string>(),
            McpServerNames = new List<string>()
        };
        return profile;
    }

    private static void Normalize(AgentProfile profile)
    {
        profile.Id = RoomAgentIds.ProfileId;
        if (string.IsNullOrWhiteSpace(profile.Name))
        {
            profile.Name = "Room Agent";
        }

        profile.ToolAllowList ??= new List<string>();
        profile.SkillNames ??= new List<string>();
        profile.McpServerNames ??= new List<string>();

        if (!profile.ToolAllowList.Any(t => string.Equals(t, "room_share_file", StringComparison.OrdinalIgnoreCase)))
        {
            profile.ToolAllowList.Add("room_share_file");
        }

        // Same as Teams workers: selecting Skills auto-attaches load_skill_through_path
        // (not shown as a catalog checkbox).
        profile.ToolAllowList.RemoveAll(t =>
            string.Equals(t, "load_skill_through_path", StringComparison.OrdinalIgnoreCase));
        if (profile.SkillNames.Any(s => !string.IsNullOrWhiteSpace(s)))
        {
            profile.ToolAllowList.Add("load_skill_through_path");
        }
    }

    /// <summary>Effective tool allow-list for a room @agent turn (includes implied skill loader).</summary>
    public static string[] ResolveToolAllowList(AgentProfile profile)
    {
        var clone = Clone(profile);
        Normalize(clone);
        return clone.ToolAllowList.Count > 0
            ? clone.ToolAllowList.ToArray()
            : CatalogTools.ToArray();
    }

    private static AgentProfile Clone(AgentProfile source) => new()
    {
        Id = source.Id,
        Name = source.Name,
        Description = source.Description,
        SoulMd = source.SoulMd,
        AgentMd = source.AgentMd,
        ModelNameOverride = source.ModelNameOverride,
        ToolAllowList = source.ToolAllowList.ToList(),
        SkillNames = source.SkillNames.ToList(),
        McpServerNames = source.McpServerNames.ToList(),
        KnowledgeModuleIds = source.KnowledgeModuleIds.ToList(),
        MaxModelToolRounds = source.MaxModelToolRounds,
        InheritSessionKnowledge = source.InheritSessionKnowledge
    };
}
