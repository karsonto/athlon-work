using System.IO;
using Athlon.Agent.Core;
using Athlon.Agent.Core.ComputerUse;
using Athlon.Agent.Infrastructure;

namespace Athlon.Agent.App.Services.ComputerUse;

/// <summary>
/// Deletes stale Computer Use frame screenshots. Every observation writes a new file that is never
/// referenced again once the frame expires, so without pruning the session attachments folder grows
/// without bound.
/// </summary>
public interface IImageAttachmentPruner
{
    Task PruneAsync(string sessionId, CancellationToken cancellationToken = default);
}

public sealed class ComputerUseAttachmentPruner(
    IAppPathProvider paths,
    AppSettings settings) : IImageAttachmentPruner
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public async Task PruneAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return;
        }

        var cfg = settings.ComputerUse;
        if (cfg.MaxScreenshotsPerSession <= 0 && cfg.ScreenshotRetentionMinutes <= 0)
        {
            return;
        }

        if (!await Gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            // Another observation is already pruning; skipping is harmless because frames only
            // accumulate one file per observation.
            return;
        }

        try
        {
            var directory = Path.Combine(paths.SessionsPath, sessionId, "attachments");
            if (!Directory.Exists(directory))
            {
                return;
            }

            // Frames carry the store's frame prefix, so user-uploaded attachments are never touched.
            var frames = new DirectoryInfo(directory)
                .EnumerateFiles(ImageAttachmentStore.FrameFilePrefix + "*", SearchOption.TopDirectoryOnly)
                .ToList();
            if (frames.Count == 0)
            {
                return;
            }

            var stale = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (cfg.ScreenshotRetentionMinutes > 0)
            {
                var cutoff = DateTime.UtcNow - TimeSpan.FromMinutes(cfg.ScreenshotRetentionMinutes);
                foreach (var file in frames.Where(file => file.LastWriteTimeUtc < cutoff))
                {
                    stale.Add(file.FullName);
                }
            }

            if (cfg.MaxScreenshotsPerSession > 0)
            {
                var keep = frames
                    .OrderByDescending(file => file.LastWriteTimeUtc)
                    .Take(cfg.MaxScreenshotsPerSession)
                    .Select(file => file.FullName)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var file in frames.Where(file => !keep.Contains(file.FullName)))
                {
                    stale.Add(file.FullName);
                }
            }

            foreach (var path in stale)
            {
                cancellationToken.ThrowIfCancellationRequested();
                TryDelete(path);
            }
        }
        catch (IOException)
        {
            // Pruning is opportunistic; a transient lock must not fail the observation.
        }
        catch (UnauthorizedAccessException)
        {
        }
        finally
        {
            Gate.Release();
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
