using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Athlon.Agent.Infrastructure.SkillHub;

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
    public void MatchesSha256_accepts_empty_expected()
    {
        Assert.True(SkillHubClient.MatchesSha256([1, 2, 3], null));
        Assert.True(SkillHubClient.MatchesSha256([1, 2, 3], ""));
    }
}
