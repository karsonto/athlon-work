using Athlon.Agent.Core.Teams;

namespace Athlon.Agent.Tests.Teams;

public sealed class TeamConfigPackageTests
{
    [Fact]
    public void Build_Serialize_Parse_RoundTripsTeamAndProfiles()
    {
        var leader = new AgentProfile
        {
            Id = "profile-leader",
            Name = "Leader",
            AgentMd = "lead",
            ToolAllowList = ["team_list_members"]
        };
        var worker = new AgentProfile
        {
            Id = "profile-worker",
            Name = "Worker",
            AgentMd = "work",
            SkillNames = ["demo"]
        };
        var team = new TeamDefinition
        {
            Id = "team-demo",
            Name = "Demo Team",
            Description = "portable",
            LeaderProfileId = leader.Id,
            WorkerProfileIds = [worker.Id],
            MaxParallelWorkers = 2
        };

        var package = TeamConfigPackageCodec.Build(team, [leader, worker]);
        var json = TeamConfigPackageCodec.Serialize(package);

        Assert.Contains("\"kind\": \"athlon.agent.team\"", json, StringComparison.Ordinal);
        Assert.Contains("\"schemaVersion\": 1", json, StringComparison.Ordinal);

        var parsed = TeamConfigPackageCodec.Parse(json);
        Assert.Equal(team.Name, parsed.Team.Name);
        Assert.Equal(2, parsed.Profiles.Count);
        Assert.Contains(parsed.Profiles, p => p.Id == leader.Id && p.Name == "Leader");
        Assert.Contains(parsed.Profiles, p => p.Id == worker.Id && p.SkillNames.Contains("demo"));
    }

    [Fact]
    public void MaterializeForImport_RemapsIdsAndKeepsGraph()
    {
        var package = TeamConfigPackageCodec.Build(
            new TeamDefinition
            {
                Id = "team-a",
                Name = "Alpha",
                LeaderProfileId = "p-lead",
                WorkerProfileIds = ["p-w1", "p-w2"]
            },
            [
                new AgentProfile { Id = "p-lead", Name = "L" },
                new AgentProfile { Id = "p-w1", Name = "W1" },
                new AgentProfile { Id = "p-w2", Name = "W2" }
            ]);

        var (team, profiles) = TeamConfigPackageCodec.MaterializeForImport(package);

        Assert.NotEqual("team-a", team.Id);
        Assert.Equal("Alpha (imported)", team.Name);
        Assert.Equal(3, profiles.Count);
        Assert.DoesNotContain(profiles, p => p.Id is "p-lead" or "p-w1" or "p-w2");
        Assert.Contains(profiles, p => p.Id == team.LeaderProfileId);
        Assert.All(team.WorkerProfileIds, id => Assert.Contains(profiles, p => p.Id == id));
    }

    [Fact]
    public void Parse_RejectsMissingLeaderProfile()
    {
        var json = """
            {
              "schemaVersion": 1,
              "kind": "athlon.agent.team",
              "team": {
                "id": "t1",
                "name": "Broken",
                "leaderProfileId": "missing",
                "workerProfileIds": []
              },
              "profiles": [
                { "id": "other", "name": "Other" }
              ]
            }
            """;

        var ex = Assert.Throws<InvalidOperationException>(() => TeamConfigPackageCodec.Parse(json));
        Assert.Contains("leaderProfileId", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}
