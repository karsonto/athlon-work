using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Compaction;
using Athlon.Agent.Core.RuntimeDiagnostics;
using Athlon.Agent.Infrastructure.BehaviorReport;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace Athlon.Agent.Infrastructure;

/// <summary>Conversation display log (conversation.jsonl).</summary>
public sealed partial class FileStorageService
{
    public async Task<ChatMessage?> TryLoadConversationMessageAsync(
        string sessionId,
        string messageId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(messageId))
        {
            return null;
        }

        using (await SessionWriteLock.AcquireAsync(sessionId, cancellationToken).ConfigureAwait(false))
        {
            var path = GetConversationDisplayPath(sessionId);
            if (!File.Exists(path))
            {
                return null;
            }

            ChatMessage? latest = null;
            foreach (var line in await File.ReadAllLinesAsync(path, cancellationToken).ConfigureAwait(false))
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                var message = ConversationDisplayLog.TryParseLine(line);
                if (message is null || !string.Equals(message.Id, messageId, StringComparison.Ordinal))
                {
                    continue;
                }

                latest = message;
            }

            return latest;
        }
    }

    public async Task AppendConversationMessageAsync(string sessionId, ChatMessage message, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return;
        }

        using (await SessionWriteLock.AcquireAsync(sessionId, cancellationToken).ConfigureAwait(false))
        {
            EnsureSessionLogDirectories(sessionId);
            var path = GetConversationDisplayPath(sessionId);
            await jsonFileStore.AppendJsonLineAsync(path, message, cancellationToken);
        }
    }

    public async Task ReplaceConversationDisplayAsync(
        string sessionId,
        IReadOnlyList<ChatMessage> messages,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return;
        }

        using (await SessionWriteLock.AcquireAsync(sessionId, cancellationToken).ConfigureAwait(false))
        {
            EnsureSessionLogDirectories(sessionId);
            var path = GetConversationDisplayPath(sessionId);
            var builder = new StringBuilder();
            foreach (var message in messages)
            {
                builder.AppendLine(JsonSerializer.Serialize(message, JsonFileStore.JsonLineOptions));
            }

            await FileIoRetry.RunAsync(
                () => File.WriteAllTextAsync(path, builder.ToString(), Utf8Bom, cancellationToken),
                cancellationToken);
        }
    }

    public async Task ReplaceConversationDisplayOffThreadAsync(
        string sessionId,
        IReadOnlyList<ChatMessage> messages,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return;
        }

        using (await SessionWriteLock.AcquireAsync(sessionId, cancellationToken).ConfigureAwait(false))
        {
            await EnsureSessionLogDirectoriesAsync(sessionId).ConfigureAwait(false);
            var path = GetConversationDisplayPath(sessionId);

            // Building the payload is the expensive half for a long conversation, so it moves with
            // the write rather than staying on the caller's thread.
            var json = await BackgroundFileIo.RunAsync(() =>
            {
                var builder = new StringBuilder();
                foreach (var message in messages)
                {
                    builder.AppendLine(JsonSerializer.Serialize(message, JsonFileStore.JsonLineOptions));
                }

                return builder.ToString();
            }).ConfigureAwait(false);

            await FileIoRetry.RunAsync(
                () => File.WriteAllTextAsync(path, json, Utf8Bom, cancellationToken),
                cancellationToken);
        }
    }

    public async Task<IReadOnlyList<ChatMessage>> LoadConversationDisplayAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return Array.Empty<ChatMessage>();
        }

        using (await SessionWriteLock.AcquireAsync(sessionId, cancellationToken).ConfigureAwait(false))
        {
            var path = GetConversationDisplayPath(sessionId);
            if (!File.Exists(path))
            {
                return Array.Empty<ChatMessage>();
            }

            // conversation.jsonl is append-only. Later lines with the same Id win so a
            // mid-turn streaming checkpoint can be overwritten by the final Persist.
            var indexById = new Dictionary<string, int>(StringComparer.Ordinal);
            var messages = new List<ChatMessage>();
            foreach (var line in await File.ReadAllLinesAsync(path, cancellationToken).ConfigureAwait(false))
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                var message = ConversationDisplayLog.TryParseLine(line);
                if (message is null)
                {
                    continue;
                }

                if (indexById.TryGetValue(message.Id, out var existingIndex))
                {
                    messages[existingIndex] = message;
                    continue;
                }

                indexById[message.Id] = messages.Count;
                messages.Add(message);
            }

            // The file is append-only so order is stable; only sort if somehow out of order.
            if (messages.Count > 1)
            {
                for (var i = 1; i < messages.Count; i++)
                {
                    if (messages[i].CreatedAt < messages[i - 1].CreatedAt)
                    {
                        messages.Sort((a, b) => a.CreatedAt.CompareTo(b.CreatedAt));
                        break;
                    }
                }
            }

            // Strip heavy tool result content for display only (full content remains in conversation.jsonl
            // for model context reconstruction).
            for (var i = 0; i < messages.Count; i++)
            {
                messages[i] = StripToolContentForDisplay(messages[i]);
            }

            return ChatMessageMemorySanitizer.SanitizeMessages(messages);
        }
    }

    public async Task<ConversationDisplayPage> LoadConversationDisplayPageAsync(
        string sessionId,
        ConversationDisplayCursor? cursor = null,
        int pageSize = ConversationDisplayLimits.PageSize,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return new ConversationDisplayPage(Array.Empty<ChatMessage>(), null);
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);

        using (await SessionWriteLock.AcquireAsync(sessionId, cancellationToken).ConfigureAwait(false))
        {
            var path = GetConversationDisplayPath(sessionId);
            if (!File.Exists(path))
            {
                return new ConversationDisplayPage(Array.Empty<ChatMessage>(), null);
            }

            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 16 * 1024,
                FileOptions.Asynchronous | FileOptions.RandomAccess);

            var endOffset = cursor is null
                ? stream.Length
                : Math.Clamp(cursor.ByteOffset, 0, stream.Length);
            var seen = new HashSet<string>(
                cursor?.SeenMessageIds ?? Array.Empty<string>(),
                StringComparer.Ordinal);
            var page = new List<ChatMessage>(pageSize);
            var reversedLine = new List<byte>();
            var buffer = new byte[16 * 1024];
            var position = endOffset;
            long? olderOffset = null;

            while (position > 0 && page.Count < pageSize)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var readStart = Math.Max(0, position - buffer.Length);
                var readLength = checked((int)(position - readStart));
                stream.Position = readStart;
                await stream.ReadExactlyAsync(buffer.AsMemory(0, readLength), cancellationToken)
                    .ConfigureAwait(false);

                for (var i = readLength - 1; i >= 0; i--)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (buffer[i] != (byte)'\n')
                    {
                        reversedLine.Add(buffer[i]);
                        continue;
                    }

                    AddReverseLine(reversedLine, page, seen);
                    reversedLine.Clear();
                    if (page.Count == pageSize)
                    {
                        olderOffset = readStart + i;
                        break;
                    }
                }

                position = readStart;
            }

            if (position == 0 && page.Count < pageSize && reversedLine.Count > 0)
            {
                AddReverseLine(reversedLine, page, seen);
                reversedLine.Clear();
            }

            page.Reverse();
            EnsureChronologicalOrder(page);
            for (var i = 0; i < page.Count; i++)
            {
                page[i] = StripToolContentForDisplay(page[i]);
            }

            var sanitized = ChatMessageMemorySanitizer.SanitizeMessages(page);
            var nextCursor = olderOffset is > 0
                ? new ConversationDisplayCursor(olderOffset.Value, seen.ToArray())
                : null;
            return new ConversationDisplayPage(sanitized, nextCursor);
        }
    }

    private static void AddReverseLine(
        List<byte> reversedLine,
        List<ChatMessage> messages,
        HashSet<string> seen)
    {
        if (reversedLine.Count == 0)
        {
            return;
        }

        reversedLine.Reverse();
        var count = reversedLine.Count;
        if (count > 0 && reversedLine[count - 1] == (byte)'\r')
        {
            count--;
        }

        var line = Encoding.UTF8.GetString(CollectionsMarshal.AsSpan(reversedLine)[..count]);
        if (line.Length > 0 && line[0] == '\uFEFF')
        {
            line = line[1..];
        }

        var message = ConversationDisplayLog.TryParseLine(line);
        // Reverse scan meets newer lines first, so first-seen Id is last-wins in file order.
        if (message is not null && seen.Add(message.Id))
        {
            messages.Add(message);
        }
    }

    private static void EnsureChronologicalOrder(List<ChatMessage> messages)
    {
        for (var i = 1; i < messages.Count; i++)
        {
            if (messages[i].CreatedAt >= messages[i - 1].CreatedAt)
            {
                continue;
            }

            messages.Sort((a, b) => a.CreatedAt.CompareTo(b.CreatedAt));
            return;
        }
    }

    public async Task ClearConversationDisplayAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return;
        }

        using (await SessionWriteLock.AcquireAsync(sessionId, cancellationToken).ConfigureAwait(false))
        {
            var path = GetConversationDisplayPath(sessionId);
            if (File.Exists(path))
            {
                await FileIoRetry.RunAsync(
                    () => AtomicFile.WriteAllTextAsync(path, string.Empty, cancellationToken),
                    cancellationToken);
            }
        }
    }

    private string GetConversationDisplayPath(string sessionId) =>
        Path.Combine(GetSessionDirectory(sessionId), "conversation.jsonl");

    private static ChatMessage StripToolContentForDisplay(ChatMessage message) =>
        ConversationDisplayContentStripper.StripToolContentForDisplay(message);
}
