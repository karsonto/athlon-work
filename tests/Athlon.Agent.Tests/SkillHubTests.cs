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
    public void CollectReusableFiles_downloads_only_manifest_entries_whose_hash_differs()
    {
        var root = Path.Combine(Path.GetTempPath(), "athlon-skill-diff", Guid.NewGuid().ToString("N"));
        var skillDir = Path.Combine(root, "other-folder");
        Directory.CreateDirectory(skillDir);
        try
        {
            var skillBody = Encoding.UTF8.GetBytes("---\nname: doc-skills\ndescription: docs\n---\n\nbody\n");
            var keptBody = Encoding.UTF8.GetBytes("{\"ok\":true}\n");
            var changedBody = Encoding.UTF8.GetBytes("{\"ok\":false}\n");
            File.WriteAllBytes(Path.Combine(skillDir, "SKILL.md"), skillBody);
            File.WriteAllBytes(Path.Combine(skillDir, "kept.json"), keptBody);
            File.WriteAllBytes(Path.Combine(skillDir, "changed.json"), changedBody);

            var remoteChanged = Encoding.UTF8.GetBytes("{\"ok\":true,\"next\":1}\n");
            var files = new[]
            {
                new SkillManifest.FileEntry("SKILL.md", skillBody.Length, SkillManifest.Sha256Hex(skillBody)),
                new SkillManifest.FileEntry("kept.json", keptBody.Length, SkillManifest.Sha256Hex(keptBody)),
                new SkillManifest.FileEntry("changed.json", remoteChanged.Length, SkillManifest.Sha256Hex(remoteChanged)),
                new SkillManifest.FileEntry("added.json", keptBody.Length, SkillManifest.Sha256Hex(keptBody))
            };

            var reusable = SkillPackageInstaller.CollectReusableFiles(skillDir, files);
            Assert.Equal(skillBody, reusable[SkillManifest.Sha256Hex(skillBody)]);
            Assert.Equal(keptBody, reusable[SkillManifest.Sha256Hex(keptBody)]);
            Assert.DoesNotContain(SkillManifest.Sha256Hex(remoteChanged), reusable.Keys);
            var missing = files
                .Select(static file => file.Sha256)
                .Where(hash => !reusable.ContainsKey(hash))
                .Distinct()
                .ToArray();
            Assert.Equal([SkillManifest.Sha256Hex(remoteChanged)], missing);
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
        var manifests = Path.Combine(root, "config", "skill-manifests");
        var anchors = Path.Combine(root, "config", "skill-anchors.json");
        var local = Path.Combine(skills, "demo");
        Directory.CreateDirectory(local);
        File.WriteAllText(Path.Combine(local, "SKILL.md"), "---\nname: demo\ndescription: local\n---\n\nlocal\n");
        var repo = new IntegritySkillRepository(skills, manifests, anchors);
        try
        {
            Assert.Empty(repo.GetAllSkills());

            var files = SkillIntegrity.ListFiles(local);
            var manifest = SkillManifest.Canonical("demo", 1, files);
            Directory.CreateDirectory(manifests);
            File.WriteAllText(Path.Combine(manifests, "demo.json"), manifest);
            var loaded = Assert.Single(repo.GetAllSkills());
            Assert.Contains("local", loaded.SkillContent, StringComparison.Ordinal);
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
    public void Manifest_over_128k_loads_without_content_check()
    {
        var root = Path.Combine(Path.GetTempPath(), "athlon-skill-catalog-large", Guid.NewGuid().ToString("N"));
        var skills = Path.Combine(root, "skills");
        var manifests = Path.Combine(root, "config", "skill-manifests");
        var anchors = Path.Combine(root, "config", "skill-anchors.json");
        var local = Path.Combine(skills, "demo");
        Directory.CreateDirectory(local);
        Directory.CreateDirectory(manifests);
        File.WriteAllText(Path.Combine(local, "SKILL.md"), "---\nname: demo\ndescription: local\n---\n\nlarge\n");
        var manifestPath = Path.Combine(manifests, "demo.json");
        var repo = new IntegritySkillRepository(skills, manifests, anchors);
        try
        {
            File.WriteAllText(manifestPath, new string('x', (int)IntegritySkillRepository.UncheckedManifestBytes));
            Assert.Empty(repo.GetAllSkills());

            File.WriteAllText(manifestPath, new string('x', (int)IntegritySkillRepository.UncheckedManifestBytes + 1));
            var loaded = Assert.Single(repo.GetAllSkills());
            Assert.Contains("large", loaded.SkillContent, StringComparison.Ordinal);
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
    public void Classify_offers_use_update_or_installed_from_local_files()
    {
        var root = Path.Combine(Path.GetTempPath(), "athlon-skill-classify", Guid.NewGuid().ToString("N"));
        var skills = Path.Combine(root, "skills");
        var local = Path.Combine(skills, "doc-skills");
        Directory.CreateDirectory(local);
        try
        {
            Assert.Equal(SkillInstallClassifier.Use, SkillInstallClassifier.Classify(null, "not-json", 0));
            Assert.Equal(SkillInstallClassifier.Use, SkillInstallClassifier.Classify(local, "not-json", 0));

            File.WriteAllText(Path.Combine(local, "SKILL.md"), "---\nname: doc-skills\ndescription: docs\n---\n\nbody\n");
            var manifest = SkillManifest.Canonical("doc-skills", 3, SkillIntegrity.ListFiles(local));
            Assert.Equal(SkillInstallClassifier.Installed, SkillInstallClassifier.Classify(local, manifest, 3));

            File.WriteAllText(Path.Combine(local, "extra.txt"), "changed");
            Assert.Equal(SkillInstallClassifier.Update, SkillInstallClassifier.Classify(local, manifest, 3));
            Assert.Equal(SkillInstallClassifier.Installed, SkillInstallClassifier.Classify(local, manifest, 4));

            var found = SkillInstallClassifier.FindInstalledDirectory(skills, new RemoteSkillDto
            {
                Id = "s1",
                EnglishName = "doc-skills",
                Name = "文档技能"
            });
            Assert.Equal(local, found);
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
}
