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

    public Task InstallAsync(RemoteSkillDto skill, CancellationToken cancellationToken = default) =>
        InstallAsync(skill, persistCatalog: true, fullDownload: false, cancellationToken);

    public Task InstallAsync(RemoteSkillDto skill, bool fullDownload, CancellationToken cancellationToken = default) =>
        InstallAsync(skill, persistCatalog: true, fullDownload, cancellationToken);

    internal async Task<bool> InstallAsync(
        RemoteSkillDto skill,
        bool persistCatalog,
        bool fullDownload,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(skill);
        if (string.IsNullOrWhiteSpace(skill.Id))
        {
            throw new ArgumentException("Skill id is required.", nameof(skill));
        }

        var manifestText = (await client.GetManifestAsync(skill.Id, cancellationToken).ConfigureAwait(false)).TrimEnd('\r', '\n');
        if (!SkillManifest.TryParse(manifestText, out var manifest) || manifest is null)
        {
            throw new InvalidOperationException("Skill manifest is invalid.");
        }

        if (manifest.CommitSeq < 1)
        {
            throw new InvalidOperationException("Skill manifest commit is invalid.");
        }

        var anchor = SkillAnchorStore.Get(paths.SkillAnchorsPath, manifest.SkillId);
        if (manifest.CommitSeq < anchor)
        {
            throw new InvalidOperationException("Skill commit is older than the anchored version.");
        }

        var manifestPath = SkillAnchorStore.ManifestPath(paths.SkillManifestDirectory, manifest.SkillId);
        var folderName = SanitizeFolderName(
            string.IsNullOrWhiteSpace(skill.EnglishName) ? manifest.SkillId : skill.EnglishName);
        paths.EnsureCreated();
        var targetDir = Path.Combine(paths.SkillsPath, folderName);
        var localName = Directory.Exists(targetDir) ? SkillFileSystemHelper.TryReadSkillName(targetDir) : null;
        if (!fullDownload
            && !string.IsNullOrWhiteSpace(localName)
            && SkillIntegrity.TryVerify(targetDir, manifestText, anchorCommitSeq: 0, localName, out _))
        {
            return false;
        }

        var localHashes = fullDownload
            ? new Dictionary<string, byte[]>(StringComparer.Ordinal)
            : IndexDirectory(targetDir);
        var missing = manifest.Files
            .Select(static file => file.Sha256)
            .Where(hash => !localHashes.ContainsKey(hash))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var stagingRoot = Path.Combine(Path.GetTempPath(), "athlon-skill-hub", IdGen.NewId());
        var assemblyDir = Path.Combine(stagingRoot, "skill");
        Directory.CreateDirectory(assemblyDir);
        try
        {
            var downloaded = missing.Length == 0
                ? new Dictionary<string, byte[]>(StringComparer.Ordinal)
                : await ReadObjectZipAsync(missing, stagingRoot, cancellationToken).ConfigureAwait(false);
            Materialize(assemblyDir, manifest, localHashes, downloaded);
            var canonical = SkillManifest.Canonical(manifest.SkillId, manifest.CommitSeq, SkillIntegrity.ListFiles(assemblyDir));
            if (!string.Equals(canonical, manifestText, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Assembled skill does not match the manifest.");
            }

            ReplaceDirectory(assemblyDir, targetDir);
            SetReadOnly(targetDir);
            Directory.CreateDirectory(paths.SkillManifestDirectory);
            await File.WriteAllTextAsync(manifestPath, manifestText, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), cancellationToken)
                .ConfigureAwait(false);
            SkillAnchorStore.Advance(paths.SkillAnchorsPath, manifest.SkillId, manifest.CommitSeq);
            _logger.Information("Installed skill package {Folder} from hub id {Id} at commit {Commit}", folderName, skill.Id, manifest.CommitSeq);
        }
        finally
        {
            TryDelete(stagingRoot);
        }

        if (persistCatalog)
        {
            await PersistCatalogAsync(cancellationToken).ConfigureAwait(false);
        }

        return true;
    }

    internal async Task PersistCatalogAsync(CancellationToken cancellationToken)
    {
        catalog.Reload();
        var installed = catalog.Skills;
        settings.Skills = SkillSettingsMerger.Merge(paths.SkillsPath, installed, settings.Skills);
        await storage.SaveSettingsAsync(settings, cancellationToken).ConfigureAwait(false);
    }

    private async Task<Dictionary<string, byte[]>> ReadObjectZipAsync(
        IReadOnlyList<string> hashes,
        string stagingRoot,
        CancellationToken cancellationToken)
    {
        var bytes = await client.DownloadObjectsAsync(hashes, cancellationToken).ConfigureAwait(false);
        if (bytes.Length == 0)
        {
            throw new InvalidOperationException("Skill object response is empty.");
        }

        var zipPath = Path.Combine(stagingRoot, "objects.zip");
        var extractDir = Path.Combine(stagingRoot, "objects");
        Directory.CreateDirectory(extractDir);
        await File.WriteAllBytesAsync(zipPath, bytes, cancellationToken).ConfigureAwait(false);
        ExtractZipSafely(zipPath, extractDir);
        var map = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(extractDir, "*", SearchOption.AllDirectories))
        {
            var content = await File.ReadAllBytesAsync(file, cancellationToken).ConfigureAwait(false);
            var hash = SkillManifest.Sha256Hex(content);
            var name = Path.GetFileName(file);
            if (!string.Equals(name, hash, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Downloaded object hash does not match its name.");
            }

            map[hash] = content;
        }

        return map;
    }

    internal static Dictionary<string, byte[]> IndexDirectory(string directory)
    {
        var map = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        if (!Directory.Exists(directory))
        {
            return map;
        }

        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            var content = File.ReadAllBytes(file);
            map[SkillManifest.Sha256Hex(content)] = content;
        }

        return map;
    }

    internal static void Materialize(
        string destination,
        SkillManifest.Parsed manifest,
        IReadOnlyDictionary<string, byte[]> localHashes,
        IReadOnlyDictionary<string, byte[]> downloaded)
    {
        foreach (var file in manifest.Files)
        {
            if (!downloaded.TryGetValue(file.Sha256, out var content)
                && !localHashes.TryGetValue(file.Sha256, out content))
            {
                throw new InvalidOperationException($"Missing skill object {file.Sha256}.");
            }

            if (content.LongLength != file.Size || !string.Equals(SkillManifest.Sha256Hex(content), file.Sha256, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Skill object {file.Sha256} does not match the manifest.");
            }

            var target = Path.GetFullPath(Path.Combine(destination, file.Path.Replace('/', Path.DirectorySeparatorChar)));
            var root = Path.GetFullPath(destination);
            EnsureUnderRoot(root, target);
            var parent = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(parent))
            {
                Directory.CreateDirectory(parent);
            }

            File.WriteAllBytes(target, content);
        }
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
            && !string.Equals(PathUtil.TrimTrailingSeparators(candidate),
                PathUtil.TrimTrailingSeparators(root),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Zip entry escapes destination: {candidate}");
        }
    }

    private static void ReplaceDirectory(string sourceDir, string destinationDir)
    {
        if (Directory.Exists(destinationDir))
        {
            ClearReadOnly(destinationDir);
            Directory.Delete(destinationDir, recursive: true);
        }

        CopyDirectory(sourceDir, destinationDir);
    }

    private static void SetReadOnly(string directory)
    {
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, File.GetAttributes(file) | FileAttributes.ReadOnly);
        }
    }

    private static void ClearReadOnly(string directory)
    {
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }
    }

    private static void TryDelete(string directory)
    {
        try
        {
            if (!Directory.Exists(directory))
            {
                return;
            }

            ClearReadOnly(directory);
            Directory.Delete(directory, recursive: true);
        }
        catch
        {
            // best-effort cleanup
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
