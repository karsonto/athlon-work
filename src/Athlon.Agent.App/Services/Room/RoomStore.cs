using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Athlon.Agent.Infrastructure;

namespace Athlon.Agent.App.Services.Room;

public sealed class RoomStore
{
    public const long MaxAttachmentBytes = 25L * 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private static readonly JsonSerializerOptions JsonlOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly string _roomsRoot;
    private readonly ConcurrentDictionary<string, RoomRuntime> _rooms = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _ioLock = new();

    public RoomStore(IAppPathProvider paths)
    {
        _roomsRoot = Path.Combine(paths.RootPath, "rooms");
        Directory.CreateDirectory(_roomsRoot);
    }

    public string RoomsRoot => _roomsRoot;

    public RoomInfo CreateRoom(string? title = null, string? workspaceRoot = null, string? workspaceId = null)
    {
        var id = Guid.NewGuid().ToString("N")[..12];
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        var info = new RoomInfo
        {
            Id = id,
            Token = token,
            Title = string.IsNullOrWhiteSpace(title) ? "Athlon 聊天房间" : title.Trim(),
            WorkspaceRoot = NormalizeOptionalPath(workspaceRoot),
            WorkspaceId = string.IsNullOrWhiteSpace(workspaceId) ? null : workspaceId.Trim(),
            CreatedAt = DateTimeOffset.UtcNow
        };

        var dir = GetRoomDirectory(id);
        Directory.CreateDirectory(dir);
        Directory.CreateDirectory(Path.Combine(dir, "files"));
        WriteRoomJson(info);
        File.WriteAllText(GetMessagesPath(id), string.Empty, Encoding.UTF8);

        _rooms[id] = new RoomRuntime(info, new List<RoomMessage>());
        return info;
    }

    public RoomInfo? UpdateWorkspace(string roomId, string? workspaceRoot, string? workspaceId = null)
    {
        var room = GetRoom(roomId);
        if (room is null)
        {
            return null;
        }

        room.WorkspaceRoot = NormalizeOptionalPath(workspaceRoot);
        room.WorkspaceId = string.IsNullOrWhiteSpace(workspaceId) ? null : workspaceId.Trim();
        WriteRoomJson(room);
        if (_rooms.TryGetValue(roomId, out var runtime))
        {
            runtime.UpdateInfo(room);
        }

        return room;
    }

    public RoomInfo? UpdateTitle(string roomId, string title)
    {
        var room = GetRoom(roomId);
        if (room is null)
        {
            return null;
        }

        var trimmed = string.IsNullOrWhiteSpace(title) ? "Athlon 聊天房间" : title.Trim();
        if (trimmed.Length > 80)
        {
            trimmed = trimmed[..80];
        }

        room.Title = trimmed;
        WriteRoomJson(room);
        if (_rooms.TryGetValue(roomId, out var runtime))
        {
            runtime.UpdateInfo(room);
        }

        return room;
    }

    private static string? NormalizeOptionalPath(string? path) =>
        string.IsNullOrWhiteSpace(path) ? null : path.Trim();

    public RoomInfo? GetRoom(string roomId)
    {
        if (_rooms.TryGetValue(roomId, out var runtime))
        {
            return runtime.Info;
        }

        var path = GetRoomJsonPath(roomId);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var json = File.ReadAllText(path);
            var info = JsonSerializer.Deserialize<RoomInfo>(json, JsonOptions);
            if (info is null || string.IsNullOrWhiteSpace(info.Id))
            {
                return null;
            }

            var messages = ReadAllMessagesFromDisk(info.Id);
            _rooms[info.Id] = new RoomRuntime(info, messages);
            return info;
        }
        catch
        {
            return null;
        }
    }

    public bool ValidateToken(string roomId, string? token)
    {
        var room = GetRoom(roomId);
        if (room is null || string.IsNullOrEmpty(token))
        {
            return false;
        }

        var expected = Encoding.UTF8.GetBytes(room.Token);
        var actual = Encoding.UTF8.GetBytes(token);
        if (expected.Length != actual.Length)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(expected, actual);
    }

    public IReadOnlyList<RoomMessage> GetMessages(string roomId, string? afterId = null)
    {
        var runtime = EnsureRuntime(roomId);
        lock (_ioLock)
        {
            var all = runtime.SnapshotMessages();
            if (string.IsNullOrWhiteSpace(afterId))
            {
                return all;
            }

            var index = all.FindIndex(m => string.Equals(m.Id, afterId, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
            {
                return all;
            }

            return all.Skip(index + 1).ToList();
        }
    }

    public RoomMessage AppendMessage(string roomId, RoomMessage message)
    {
        var runtime = EnsureRuntime(roomId);
        lock (_ioLock)
        {
            if (string.IsNullOrWhiteSpace(message.Id))
            {
                message.Id = Guid.NewGuid().ToString("N");
            }

            if (message.CreatedAt == default)
            {
                message.CreatedAt = DateTimeOffset.UtcNow;
            }

            runtime.AddMessage(CloneMessage(message));
            var line = JsonSerializer.Serialize(message, JsonlOptions);
            File.AppendAllText(GetMessagesPath(roomId), line + Environment.NewLine, Encoding.UTF8);
            runtime.Raise(new RoomStoreEvent(RoomStoreEventKind.Message, CloneMessage(message)));
            return message;
        }
    }

    public RoomMessage? UpdateMessageText(string roomId, string messageId, string? text)
    {
        var runtime = EnsureRuntime(roomId);
        lock (_ioLock)
        {
            var updated = runtime.UpdateText(messageId, text);
            if (updated is null)
            {
                return null;
            }

            RewriteMessagesFile(roomId, runtime.SnapshotMessages());
            runtime.Raise(new RoomStoreEvent(RoomStoreEventKind.MessageUpdated, CloneMessage(updated)));
            return updated;
        }
    }

    public RoomAttachmentInfo SaveAttachment(
        string roomId,
        string fileName,
        string mimeType,
        Stream content,
        long? declaredLength = null)
    {
        EnsureRuntime(roomId);
        var safeName = SanitizeFileName(fileName);
        var attachmentId = Guid.NewGuid().ToString("N");
        var dir = Path.Combine(GetRoomDirectory(roomId), "files", attachmentId);
        Directory.CreateDirectory(dir);
        var storedPath = Path.Combine(dir, safeName);

        long size;
        using (var fs = File.Create(storedPath))
        {
            var buffer = new byte[81920];
            size = 0;
            int read;
            while ((read = content.Read(buffer, 0, buffer.Length)) > 0)
            {
                size += read;
                if (size > MaxAttachmentBytes)
                {
                    fs.Dispose();
                    try
                    {
                        Directory.Delete(dir, recursive: true);
                    }
                    catch
                    {
                        // best effort
                    }

                    throw new InvalidOperationException($"Attachment exceeds {MaxAttachmentBytes} bytes.");
                }

                fs.Write(buffer, 0, read);
            }
        }

        _ = declaredLength;

        var isImage = mimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
            || IsImageExtension(Path.GetExtension(safeName));

        var info = new RoomAttachmentInfo
        {
            Id = attachmentId,
            FileName = safeName,
            MimeType = string.IsNullOrWhiteSpace(mimeType) ? GuessMime(safeName) : mimeType,
            Size = size,
            IsImage = isImage,
            StoredName = safeName
        };

        WriteAttachmentMeta(roomId, info);
        return info;
    }

    /// <summary>
    /// Copy a room attachment into the host workspace so @agent can read/analyze it.
    /// Layout: {workspaceRoot}/.athlon-room/{roomId}/{fileName}
    /// </summary>
    public RoomAttachmentInfo? MirrorAttachmentToWorkspace(string roomId, string attachmentId, string workspaceRoot)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot))
        {
            return null;
        }

        var info = GetAttachmentMeta(roomId, attachmentId);
        var sourcePath = GetAttachmentFilePath(roomId, attachmentId);
        if (info is null || sourcePath is null)
        {
            return null;
        }

        string targetDir;
        string targetPath;
        try
        {
            var rootFull = Path.GetFullPath(workspaceRoot.Trim());
            targetDir = Path.Combine(rootFull, ".athlon-room", roomId);
            Directory.CreateDirectory(targetDir);

            var safeName = SanitizeFileName(info.FileName);
            targetPath = Path.Combine(targetDir, safeName);
            if (File.Exists(targetPath))
            {
                var stem = Path.GetFileNameWithoutExtension(safeName);
                var ext = Path.GetExtension(safeName);
                targetPath = Path.Combine(targetDir, $"{stem}_{attachmentId[..Math.Min(8, attachmentId.Length)]}{ext}");
            }

            var targetFull = Path.GetFullPath(targetPath);
            if (!targetFull.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(targetFull, rootFull, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            File.Copy(sourcePath, targetFull, overwrite: true);
            info.WorkspacePath = targetFull;
            WriteAttachmentMeta(roomId, info);
            return info;
        }
        catch
        {
            return info;
        }
    }

    public void WriteAttachmentMeta(string roomId, RoomAttachmentInfo info)
    {
        if (!IsSafeId(roomId) || !IsSafeId(info.Id))
        {
            return;
        }

        var metaPath = Path.Combine(GetRoomDirectory(roomId), "files", info.Id, "meta.json");
        Directory.CreateDirectory(Path.GetDirectoryName(metaPath)!);
        File.WriteAllText(metaPath, JsonSerializer.Serialize(info, JsonOptions), Encoding.UTF8);
    }

    public RoomAttachmentInfo SaveAttachmentFromPath(string roomId, string absolutePath)
    {
        if (!File.Exists(absolutePath))
        {
            throw new FileNotFoundException("File not found.", absolutePath);
        }

        var info = new FileInfo(absolutePath);
        if (info.Length > MaxAttachmentBytes)
        {
            throw new InvalidOperationException($"Attachment exceeds {MaxAttachmentBytes} bytes.");
        }

        var fileName = info.Name;
        var mime = GuessMime(fileName);
        using var stream = File.OpenRead(absolutePath);
        return SaveAttachment(roomId, fileName, mime, stream, info.Length);
    }

    public RoomAttachmentInfo? GetAttachmentMeta(string roomId, string attachmentId)
    {
        if (!IsSafeId(roomId) || !IsSafeId(attachmentId))
        {
            return null;
        }

        var metaPath = Path.Combine(GetRoomDirectory(roomId), "files", attachmentId, "meta.json");
        if (!File.Exists(metaPath))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<RoomAttachmentInfo>(File.ReadAllText(metaPath), JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    public string? GetAttachmentFilePath(string roomId, string attachmentId)
    {
        var meta = GetAttachmentMeta(roomId, attachmentId);
        if (meta is null)
        {
            return null;
        }

        var path = Path.Combine(GetRoomDirectory(roomId), "files", attachmentId, meta.StoredName);
        var root = Path.GetFullPath(GetRoomDirectory(roomId));
        var full = Path.GetFullPath(path);
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(full, root, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return File.Exists(full) ? full : null;
    }

    public IDisposable Subscribe(string roomId, Action<RoomMessage> handler) =>
        SubscribeEvents(roomId, ev =>
        {
            if (ev.Kind == RoomStoreEventKind.Message)
            {
                handler(ev.Message);
            }
        });

    public IDisposable SubscribeEvents(string roomId, Action<RoomStoreEvent> handler)
    {
        var runtime = EnsureRuntime(roomId);
        return runtime.Subscribe(handler);
    }

    public void InvalidateToken(string roomId)
    {
        var room = GetRoom(roomId);
        if (room is null)
        {
            return;
        }

        room.Token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        WriteRoomJson(room);
        if (_rooms.TryGetValue(roomId, out var runtime))
        {
            runtime.UpdateInfo(room);
        }
    }

    private RoomRuntime EnsureRuntime(string roomId)
    {
        var info = GetRoom(roomId) ?? throw new InvalidOperationException("Room not found.");
        return _rooms.GetOrAdd(info.Id, id => new RoomRuntime(info, ReadAllMessagesFromDisk(id)));
    }

    private List<RoomMessage> ReadAllMessagesFromDisk(string roomId)
    {
        var path = GetMessagesPath(roomId);
        if (!File.Exists(path))
        {
            return new List<RoomMessage>();
        }

        var list = new List<RoomMessage>();
        foreach (var line in File.ReadLines(path, Encoding.UTF8))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                var msg = JsonSerializer.Deserialize<RoomMessage>(line, JsonlOptions);
                if (msg is not null)
                {
                    list.Add(msg);
                }
            }
            catch
            {
                // skip corrupt line
            }
        }

        return list;
    }

    private void RewriteMessagesFile(string roomId, IReadOnlyList<RoomMessage> messages)
    {
        var path = GetMessagesPath(roomId);
        var sb = new StringBuilder();
        foreach (var message in messages)
        {
            sb.AppendLine(JsonSerializer.Serialize(message, JsonlOptions));
        }

        File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
    }

    private void WriteRoomJson(RoomInfo info)
    {
        var path = GetRoomJsonPath(info.Id);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(info, JsonOptions), Encoding.UTF8);
    }

    private string GetRoomDirectory(string roomId)
    {
        if (!IsSafeId(roomId))
        {
            throw new ArgumentException("Invalid room id.", nameof(roomId));
        }

        return Path.Combine(_roomsRoot, roomId);
    }

    private string GetRoomJsonPath(string roomId) => Path.Combine(GetRoomDirectory(roomId), "room.json");

    private string GetMessagesPath(string roomId) => Path.Combine(GetRoomDirectory(roomId), "messages.jsonl");

    private static RoomMessage CloneMessage(RoomMessage message) => new()
    {
        Id = message.Id,
        SenderId = message.SenderId,
        DisplayName = message.DisplayName,
        Kind = message.Kind,
        Text = message.Text,
        Attachments = message.Attachments.Select(a => new RoomAttachmentInfo
        {
            Id = a.Id,
            FileName = a.FileName,
            MimeType = a.MimeType,
            Size = a.Size,
            IsImage = a.IsImage,
            StoredName = a.StoredName
        }).ToList(),
        CreatedAt = message.CreatedAt
    };

    internal static bool IsSafeId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 64)
        {
            return false;
        }

        foreach (var ch in id)
        {
            if (!char.IsAsciiLetterOrDigit(ch) && ch is not '-' and not '_')
            {
                return false;
            }
        }

        return true;
    }

    internal static string SanitizeFileName(string fileName)
    {
        var name = Path.GetFileName(fileName.Replace('\\', '/'));
        if (string.IsNullOrWhiteSpace(name) || name is "." or "..")
        {
            return "file.bin";
        }

        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(name.Length);
        foreach (var ch in name)
        {
            sb.Append(Array.IndexOf(invalid, ch) >= 0 ? '_' : ch);
        }

        var cleaned = sb.ToString().Trim();
        return string.IsNullOrWhiteSpace(cleaned) ? "file.bin" : cleaned;
    }

    private static bool IsImageExtension(string ext) =>
        ext.Equals(".png", StringComparison.OrdinalIgnoreCase)
        || ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
        || ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)
        || ext.Equals(".gif", StringComparison.OrdinalIgnoreCase)
        || ext.Equals(".webp", StringComparison.OrdinalIgnoreCase)
        || ext.Equals(".bmp", StringComparison.OrdinalIgnoreCase);

    private static string GuessMime(string fileName)
    {
        return Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".pdf" => "application/pdf",
            ".txt" => "text/plain",
            ".json" => "application/json",
            ".zip" => "application/zip",
            ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            ".doc" => "application/msword",
            _ => "application/octet-stream"
        };
    }

    private sealed class RoomRuntime
    {
        private readonly object _gate = new();
        private readonly List<Action<RoomStoreEvent>> _handlers = new();
        private readonly List<RoomMessage> _messages;

        public RoomRuntime(RoomInfo info, List<RoomMessage> messages)
        {
            Info = info;
            _messages = messages;
        }

        public RoomInfo Info { get; private set; }

        public void UpdateInfo(RoomInfo info) => Info = info;

        public List<RoomMessage> SnapshotMessages()
        {
            lock (_gate)
            {
                return _messages.Select(CloneMessage).ToList();
            }
        }

        public void AddMessage(RoomMessage message)
        {
            lock (_gate)
            {
                _messages.Add(message);
            }
        }

        public RoomMessage? UpdateText(string messageId, string? text)
        {
            lock (_gate)
            {
                var index = _messages.FindIndex(m =>
                    string.Equals(m.Id, messageId, StringComparison.OrdinalIgnoreCase));
                if (index < 0)
                {
                    return null;
                }

                _messages[index].Text = text;
                return CloneMessage(_messages[index]);
            }
        }

        public IDisposable Subscribe(Action<RoomStoreEvent> handler)
        {
            lock (_gate)
            {
                _handlers.Add(handler);
            }

            return new Unsubscriber(() =>
            {
                lock (_gate)
                {
                    _handlers.Remove(handler);
                }
            });
        }

        public void Raise(RoomStoreEvent storeEvent)
        {
            Action<RoomStoreEvent>[] snapshot;
            lock (_gate)
            {
                snapshot = _handlers.ToArray();
            }

            foreach (var handler in snapshot)
            {
                try
                {
                    handler(storeEvent);
                }
                catch
                {
                    // ignore subscriber errors
                }
            }
        }

        private sealed class Unsubscriber(Action unsubscribe) : IDisposable
        {
            public void Dispose() => unsubscribe();
        }
    }
}
