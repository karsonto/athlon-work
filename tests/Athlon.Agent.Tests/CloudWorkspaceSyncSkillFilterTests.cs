using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using Athlon.Agent.App.Services.Room;

namespace Athlon.Agent.Tests;

public sealed class CloudWorkspaceSyncSkillFilterTests
{
    [Fact]
    public void ResolveSelectedSkillFolders_MapsFrontmatterName_ToFolder()
    {
        using var root = new TempDir();
        var folder = Path.Combine(root.Path, "english-folder");
        Directory.CreateDirectory(folder);
        File.WriteAllText(
            Path.Combine(folder, "SKILL.md"),
            "---\nname: 中文技能\ndescription: demo\n---\n\nbody\n",
            Encoding.UTF8);

        var folders = CloudWorkspaceSync.ResolveSelectedSkillFolders(root.Path, ["中文技能"]);
        Assert.Equal(["english-folder"], folders);
    }

    [Fact]
    public void ResolveSelectedSkillFolders_EmptySelection_IsEmpty_NotAll()
    {
        using var root = new TempDir();
        Directory.CreateDirectory(Path.Combine(root.Path, "a"));
        File.WriteAllText(Path.Combine(root.Path, "a", "SKILL.md"), "---\nname: a\ndescription: d\n---\n\nx\n");

        Assert.Empty(CloudWorkspaceSync.ResolveSelectedSkillFolders(root.Path, null));
        Assert.Empty(CloudWorkspaceSync.ResolveSelectedSkillFolders(root.Path, []));
    }

    [Fact]
    public void PackDirectoryTarGz_EmptyAllowList_PacksNothing()
    {
        using var root = new TempDir();
        var skill = Path.Combine(root.Path, "keep");
        Directory.CreateDirectory(skill);
        File.WriteAllText(Path.Combine(skill, "SKILL.md"), "---\nname: keep\ndescription: d\n---\n\nbody\n");

        using var tar = CloudWorkspaceSync.PackDirectoryTarGz(root.Path, onlyTopLevelNames: []);
        using var gzip = new GZipStream(tar, CompressionMode.Decompress, leaveOpen: true);
        using var reader = new TarReader(gzip);
        Assert.Null(reader.GetNextEntry());
    }

    [Fact]
    public void BuildManifest_EmptyAllowList_ExcludesAllSkills()
    {
        using var root = new TempDir();
        var skill = Path.Combine(root.Path, "keep");
        Directory.CreateDirectory(skill);
        File.WriteAllText(Path.Combine(skill, "SKILL.md"), "---\nname: keep\ndescription: d\n---\n\nbody\n");

        var all = CloudWorkspaceSync.BuildManifest(root.Path, onlyTopLevelNames: null);
        Assert.Contains(all, e => e.Path.StartsWith("keep/", StringComparison.Ordinal));

        var none = CloudWorkspaceSync.BuildManifest(root.Path, onlyTopLevelNames: []);
        Assert.Empty(none);
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "athlon-skill-filter-" + Guid.NewGuid().ToString("N"));

        public TempDir() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch
            {
                // ignore
            }
        }
    }
}
