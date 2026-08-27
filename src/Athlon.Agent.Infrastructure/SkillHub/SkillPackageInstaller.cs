using System.IO.Compression;
using System.Text;
using Athlon.Agent.Core;
using Athlon.Agent.Skills;

namespace Athlon.Agent.Infrastructure.SkillHub;

public sealed class SkillPackageInstaller(
    ISkillHubClient client,
    IAppPathProvider paths,
    IAgentSkillCatalog catalog,
    AppSettings settings,
    IFileStorageService storage,
    IAppLogger logger)
{
    private readonly IAppLogger _logger = logger.ForContext("SkillPackageInstaller");

    public async Task InstallAsync(RemoteSkillDto skill, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(skill);
        if (string.IsNullOrWhiteSpace(skill.Id))
        {
            throw new ArgumentException("Skill id is required.", nameof(skill));
        }

        var folderName = SanitizeFolderName(
            string.IsNullOrWhiteSpace(skill.EnglishName) ? skill.Id : skill.EnglishName);
        var bytes = await client.DownloadAsync(skill.Id, cancellationToken).ConfigureAwait(false);
        if (bytes.Length == 0)
        {
            throw new InvalidOperationException("Downloaded skill package is empty.");
        }

        if (!SkillHubClient.MatchesSha256(bytes, skill.PackageSha256))
        {
            throw new InvalidOperationException("Skill package SHA-256 mismatch.");
        }

        paths.EnsureCreated();
        var stagingRoot = Path.Combine(Path.GetTempPath(), "athlon-skill-hub", Guid.NewGuid().ToString("N"));
        var extractDir = Path.Combine(stagingRoot, "extract");
        var zipPath = Path.Combine(stagingRoot, "package.zip");
        Directory.CreateDirectory(extractDir);

        try
        {
            await File.WriteAllBytesAsync(zipPath, bytes, cancellationToken).ConfigureAwait(false);
            ExtractZipSafely(zipPath, extractDir);

            var skillSourceDir = LocateSkillDirectory(extractDir)
                ?? throw new InvalidOperationException("SKILL.md not found in package.");

            var targetDir = Path.Combine(paths.SkillsPath, folderName);
            if (Directory.Exists(targetDir))
            {
                Directory.Delete(targetDir, recursive: true);
            }

            CopyDirectory(skillSourceDir, targetDir);
            _logger.Information("Installed skill package {Folder} from hub id {Id}", folderName, skill.Id);
        }
        finally
        {
            try
            {
                if (Directory.Exists(stagingRoot))
                {
                    Directory.Delete(stagingRoot, recursive: true);
                }
            }
            catch
            {
                // best-effort cleanup
            }
        }

        catalog.Reload();
        var installed = catalog.Skills;
        settings.Skills = SkillSettingsMerger.Merge(paths.SkillsPath, installed, settings.Skills);
        await storage.SaveSettingsAsync(settings, cancellationToken).ConfigureAwait(false);
    }

    internal static string SanitizeFolderName(string name)
    {
        var trimmed = name.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            throw new ArgumentException("Skill folder name is required.");
        }

        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(trimmed.Length);
        foreach (var ch in trimmed)
        {
            builder.Append(invalid.Contains(ch) || ch is '/' or '\\' ? '_' : ch);
        }

        var sanitized = builder.ToString().Trim('.', ' ');
        if (string.IsNullOrWhiteSpace(sanitized) || sanitized is "." or "..")
        {
            throw new ArgumentException($"Invalid skill folder name: '{name}'.");
        }

        return sanitized;
    }

    internal static void ExtractZipSafely(string zipPath, string destinationDirectory)
    {
        var destinationRoot = Path.GetFullPath(destinationDirectory);
        Directory.CreateDirectory(destinationRoot);

        using var archive = ZipFile.OpenRead(zipPath);
        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name)
                && (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\')))
            {
                var dirPath = Path.GetFullPath(Path.Combine(destinationRoot, entry.FullName));
                EnsureUnderRoot(destinationRoot, dirPath);
                Directory.CreateDirectory(dirPath);
                continue;
            }

            var targetPath = Path.GetFullPath(Path.Combine(destinationRoot, entry.FullName));
            EnsureUnderRoot(destinationRoot, targetPath);
            var parent = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrEmpty(parent))
            {
                Directory.CreateDirectory(parent);
            }

            entry.ExtractToFile(targetPath, overwrite: true);
        }
    }

    internal static string? LocateSkillDirectory(string extractRoot)
    {
        var direct = Path.Combine(extractRoot, SkillUtil.SkillFileName);
        if (File.Exists(direct))
        {
            return extractRoot;
        }

        foreach (var dir in Directory.EnumerateDirectories(extractRoot))
        {
            if (File.Exists(Path.Combine(dir, SkillUtil.SkillFileName)))
            {
                return dir;
            }
        }

        foreach (var dir in Directory.EnumerateDirectories(extractRoot, "*", SearchOption.AllDirectories))
        {
            if (File.Exists(Path.Combine(dir, SkillUtil.SkillFileName)))
            {
                return dir;
            }
        }

        return null;
    }

    private static void EnsureUnderRoot(string root, string candidate)
    {
        var rootFull = Path.GetFullPath(root);
        if (!rootFull.EndsWith(Path.DirectorySeparatorChar)
            && !rootFull.EndsWith(Path.AltDirectorySeparatorChar))
        {
            rootFull += Path.DirectorySeparatorChar;
        }

        if (!candidate.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(candidate.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Zip entry escapes destination: {candidate}");
        }
    }

    private static void CopyDirectory(string sourceDir, string destinationDir)
    {
        Directory.CreateDirectory(destinationDir);
        foreach (var file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceDir, file);
            var target = Path.Combine(destinationDir, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }
}
