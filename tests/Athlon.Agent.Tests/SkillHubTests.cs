using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Athlon.Agent.Infrastructure.SkillHub;
using Athlon.Agent.Skills;

namespace Athlon.Agent.Tests;

public sealed class SkillHubTests
{
    [Fact]
    public void RemoteSkillListResponse_deserializes_category_and_position()
    {
        const string json = """
            {
              "items": [
                {
                  "id": "s1",
                  "englishName": "doc-skills",
                  "name": "文档技能",
                  "description": "docs",
                  "category": "对公业务",
                  "position": "客户经理",
                  "packageSize": 12,
                  "packageSha256": "abc",
                  "download": "/agent/skills/download?id=s1"
                }
              ]
            }
            """;

        var payload = JsonSerializer.Deserialize<RemoteSkillListResponse>(
            json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        Assert.NotNull(payload);
        var item = Assert.Single(payload!.Items);
        Assert.Equal("s1", item.Id);
        Assert.Equal("doc-skills", item.EnglishName);
        Assert.Equal("对公业务", item.Category);
        Assert.Equal("客户经理", item.Position);
    }

    [Fact]
    public void SanitizeFolderName_rejects_traversal_like_names()
    {
        Assert.Throws<ArgumentException>(() => SkillPackageInstaller.SanitizeFolderName(".."));
        Assert.Equal("doc_skills", SkillPackageInstaller.SanitizeFolderName("doc/skills"));
    }

    [Fact]
    public void ExtractZipSafely_and_LocateSkillDirectory_handles_nested_folder()
    {
        var root = Path.Combine(Path.GetTempPath(), "athlon-skill-hub-tests", Guid.NewGuid().ToString("N"));
        var zipPath = Path.Combine(root, "pkg.zip");
        var extractDir = Path.Combine(root, "out");
        Directory.CreateDirectory(root);

        try
        {
            using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                var entry = zip.CreateEntry("my-skill/SKILL.md");
                using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
                writer.Write("---\nname: my-skill\ndescription: test\n---\n\n# Hello\n");
            }

            SkillPackageInstaller.ExtractZipSafely(zipPath, extractDir);
            var skillDir = SkillPackageInstaller.LocateSkillDirectory(extractDir);
            Assert.NotNull(skillDir);
            Assert.True(File.Exists(Path.Combine(skillDir!, "SKILL.md")));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void ExtractZipSafely_rejects_zip_slip()
    {
        var root = Path.Combine(Path.GetTempPath(), "athlon-skill-hub-tests", Guid.NewGuid().ToString("N"));
        var zipPath = Path.Combine(root, "evil.zip");
        var extractDir = Path.Combine(root, "out");
        Directory.CreateDirectory(root);

        try
        {
            using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                var entry = zip.CreateEntry("../escape.txt");
                using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
                writer.Write("nope");
            }

            Assert.Throws<InvalidOperationException>(
                () => SkillPackageInstaller.ExtractZipSafely(zipPath, extractDir));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void MatchesSha256_rejects_empty_expected()
    {
        Assert.False(SkillHubClient.MatchesSha256([1, 2, 3], null));
        Assert.False(SkillHubClient.MatchesSha256([1, 2, 3], ""));
    }

    [Fact]
    public void Canonical_manifest_matches_shared_vector()
    {
        var sha = new string('a', 64);
        var json = SkillManifest.Canonical("demo", 1, [new SkillManifest.FileEntry("SKILL.md", 4, sha)]);
        Assert.Equal(
            "{\"commitSeq\":1,\"files\":[{\"path\":\"SKILL.md\",\"sha256\":\"" + sha + "\",\"size\":4}],\"skillId\":\"demo\"}",
            json);
        Assert.Equal(
            "{\"commitSeq\":2,\"files\":[],\"skillId\":\"技能\"}",
            SkillManifest.Canonical("技能", 2, []));
    }

    [Fact]
    public void Materialize_reuses_local_bytes_and_verify_rejects_extra_or_rollback()
    {
        var body = Encoding.UTF8.GetBytes("hello");
        var sha = SkillManifest.Sha256Hex(body);
        var manifestText = SkillManifest.Canonical("demo", 1, [new SkillManifest.FileEntry("SKILL.md", body.Length, sha)]);
        Assert.True(SkillManifest.TryParse(manifestText, out var manifest));
        var root = Path.Combine(Path.GetTempPath(), "athlon-skill-integrity", Guid.NewGuid().ToString("N"));
        var dest = Path.Combine(root, "skill");
        Directory.CreateDirectory(root);
        try
        {
            SkillPackageInstaller.Materialize(
                dest,
                manifest!,
                new Dictionary<string, byte[]> { [sha] = body },
                new Dictionary<string, byte[]>());
            Assert.True(SkillIntegrity.TryVerify(dest, manifestText, 0, "demo", out var error), error);
            Assert.False(SkillIntegrity.TryVerify(dest, manifestText, 2, "demo", out _));
            File.WriteAllText(Path.Combine(dest, "extra.txt"), "x");
            Assert.False(SkillIntegrity.TryVerify(dest, manifestText, 0, "demo", out _));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void Unsigned_skill_stays_out_of_catalog_until_manifest_matches()
    {
        var root = Path.Combine(Path.GetTempPath(), "athlon-skill-catalog", Guid.NewGuid().ToString("N"));
        var skills = Path.Combine(root, "skills");
        var hub = Path.Combine(root, "skills-hub");
        var manifests = Path.Combine(root, "config", "skill-manifests");
        var anchors = Path.Combine(root, "config", "skill-anchors.json");
        var local = Path.Combine(skills, "demo");
        Directory.CreateDirectory(local);
        File.WriteAllText(Path.Combine(local, "SKILL.md"), "---\nname: demo\ndescription: local\n---\n\nlocal\n");
        var repo = new IntegritySkillRepository(skills, hub, manifests, anchors);
        try
        {
            Assert.Empty(repo.GetAllSkills());

            var files = SkillIntegrity.ListFiles(local);
            var manifest = SkillManifest.Canonical("demo", 1, files);
            Directory.CreateDirectory(manifests);
            File.WriteAllText(Path.Combine(manifests, "demo.json"), manifest);
            var loaded = Assert.Single(repo.GetAllSkills());
            Assert.Contains("local", loaded.SkillContent, StringComparison.Ordinal);

            var hubDir = Path.Combine(hub, "demo");
            Directory.CreateDirectory(hubDir);
            File.WriteAllText(Path.Combine(hubDir, "SKILL.md"), "---\nname: demo\ndescription: hub\n---\n\nhub\n");
            var hubManifest = SkillManifest.Canonical("demo", 2, SkillIntegrity.ListFiles(hubDir));
            File.WriteAllText(Path.Combine(manifests, "demo.json"), hubManifest);
            File.WriteAllText(anchors, "{\"demo\":2}");
            loaded = Assert.Single(repo.GetAllSkills());
            Assert.Contains("hub", loaded.SkillContent, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void Select_skips_remote_catalog_when_no_local_manifest_exists()
    {
        var remote = new RemoteSkillDto { Id = "s1", EnglishName = "demo", CommitSeq = 3 };

        Assert.Empty(SkillUpdateSelector.Select([], [remote]));
    }

    [Fact]
    public void Select_skips_remote_skills_that_are_not_installed()
    {
        var local = Revisions(("demo", 1));
        var remote = new RemoteSkillDto { Id = "s9", EnglishName = "other", Name = "其他", CommitSeq = 4 };

        Assert.Empty(SkillUpdateSelector.Select(local, [remote]));
    }

    [Fact]
    public void Select_skips_remote_skills_at_or_behind_the_local_commit()
    {
        var local = Revisions(("demo", 2));
        var same = new RemoteSkillDto { Id = "s1", EnglishName = "demo", CommitSeq = 2 };
        var older = new RemoteSkillDto { Id = "s1", EnglishName = "demo", CommitSeq = 1 };

        Assert.Empty(SkillUpdateSelector.Select(local, [same, older]));
    }

    [Fact]
    public void Select_includes_a_newer_remote_commit_matched_by_english_name()
    {
        var local = Revisions(("demo", 1));
        var installed = new RemoteSkillDto { Id = "s1", EnglishName = "demo", CommitSeq = 3 };
        var other = new RemoteSkillDto { Id = "s2", EnglishName = "other", CommitSeq = 9 };

        var selected = SkillUpdateSelector.Select(local, [installed, other]);

        var skill = Assert.Single(selected);
        Assert.Equal("s1", skill.Id);
    }

    [Fact]
    public void Select_includes_a_matched_skill_when_the_list_omits_commit_seq()
    {
        var local = Revisions(("demo", 4));
        var remote = new RemoteSkillDto { Id = "s1", EnglishName = "doc/skills", Name = "demo" };

        var selected = SkillUpdateSelector.Select(local, [remote]);

        Assert.Single(selected);
    }

    [Fact]
    public void Select_matches_sanitized_hub_folder_when_platform_id_differs()
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "demo", "doc_skills" };
        var local = new[] { new SkillUpdateSelector.LocalSkillRevision("demo", 1, keys) };
        var remote = new RemoteSkillDto { Id = "s1", EnglishName = "doc/skills", CommitSeq = 2 };

        var selected = SkillUpdateSelector.Select(local, [remote]);

        Assert.Equal("s1", Assert.Single(selected).Id);
    }

    [Fact]
    public void ReadLocalRevisions_indexes_manifest_commit_and_hub_folder()
    {
        var root = Path.Combine(Path.GetTempPath(), "athlon-skill-sync", Guid.NewGuid().ToString("N"));
        var manifests = Path.Combine(root, "skill-manifests");
        var hub = Path.Combine(root, "skills-hub", "doc-skills");
        Directory.CreateDirectory(manifests);
        Directory.CreateDirectory(hub);
        try
        {
            File.WriteAllText(
                Path.Combine(manifests, "demo.json"),
                SkillManifest.Canonical("demo", 2, []));
            File.WriteAllText(Path.Combine(hub, "SKILL.md"), "---\nname: demo\ndescription: hub\n---\n\nhub\n");

            var revision = Assert.Single(SkillUpdateSelector.ReadLocalRevisions(manifests, Path.Combine(root, "skills-hub")));
            Assert.Equal("demo", revision.SkillId);
            Assert.Equal(2, revision.CommitSeq);
            Assert.Contains("doc-skills", revision.MatchKeys);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void ReadWireId_supports_string_and_number()
    {
        using var stringDoc = JsonDocument.Parse("""{"type":"add","id":"abc-1"}""");
        Assert.Equal("abc-1", Athlon.Agent.App.ViewModels.SkillHubViewModel.ReadWireId(stringDoc.RootElement));

        using var numberDoc = JsonDocument.Parse("""{"type":"add","id":42}""");
        Assert.Equal("42", Athlon.Agent.App.ViewModels.SkillHubViewModel.ReadWireId(numberDoc.RootElement));
    }

    [Fact]
    public void UnwrapWebMessageJson_unwraps_stringified_payload()
    {
        var wrapped = JsonSerializer.Serialize("""{"type":"add","id":"s1"}""");
        var unwrapped = Athlon.Agent.App.Views.SkillHubPageView.UnwrapWebMessageJson(wrapped);
        Assert.Equal("""{"type":"add","id":"s1"}""", unwrapped);

        var objectJson = """{"type":"ready"}""";
        Assert.Equal(objectJson, Athlon.Agent.App.Views.SkillHubPageView.UnwrapWebMessageJson(objectJson));
    }

    private static IReadOnlyList<SkillUpdateSelector.LocalSkillRevision> Revisions(
        params (string SkillId, long CommitSeq)[] skills) =>
        skills.Select(static skill => new SkillUpdateSelector.LocalSkillRevision(
                skill.SkillId,
                skill.CommitSeq,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { skill.SkillId }))
            .ToArray();
}
