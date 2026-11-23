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
        "room_share_file"
    ];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly string _path;
    private readonly object _gate = new();
    private AgentProfile? _cached;

    public RoomAgentProfileStore(IAppPathProvider paths)
    {
        Directory.CreateDirectory(paths.ConfigPath);
        _path = Path.Combine(paths.ConfigPath, "room-agent-profile.json");
    }

    public AgentProfile GetOrCreate()
    {
        lock (_gate)
        {
            if (_cached is not null)
            {
                return Clone(_cached);
            }

            if (File.Exists(_path))
            {
                try
                {
                    var loaded = JsonSerializer.Deserialize<AgentProfile>(File.ReadAllText(_path), JsonOptions);
                    if (loaded is not null)
                    {
                        Normalize(loaded);
                        _cached = loaded;
                        return Clone(loaded);
                    }
                }
                catch
                {
                    // fall through to default
                }
            }

            var created = CreateDefault();
            PersistUnlocked(created);
            _cached = created;
            return Clone(created);
        }
    }

    public void Save(AgentProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        Normalize(profile);
        lock (_gate)
        {
            PersistUnlocked(profile);
            _cached = Clone(profile);
        }
    }

    private void PersistUnlocked(AgentProfile profile)
    {
        File.WriteAllText(_path, JsonSerializer.Serialize(profile, JsonOptions));
    }

    private static AgentProfile CreateDefault()
    {
        var profile = new AgentProfile
        {
            Id = RoomAgentIds.ProfileId,
            Name = "Room Agent",
            Description = "LAN chat room agent",
            AgentMd =
                "You are Athlon in a shared LAN chat room. Reply concisely like a teammate. "
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

        // Always allow room_share_file when any file tools are enabled, else always include it.
        if (!profile.ToolAllowList.Any(t => string.Equals(t, "room_share_file", StringComparison.OrdinalIgnoreCase)))
        {
            profile.ToolAllowList.Add("room_share_file");
        }
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
