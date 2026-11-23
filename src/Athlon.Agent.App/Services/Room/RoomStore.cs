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

    public RoomInfo CreateRoom(string? title = null)
    {
        var id = Guid.NewGuid().ToString("N")[..12];
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        var info = new RoomInfo
        {
            Id = id,
            Token = token,
            Title = string.IsNullOrWhiteSpace(title) ? "Athlon 聊天房间" : title.Trim(),
            CreatedAt = DateTimeOffset.UtcNow
        };

        var dir = GetRoomDirectory(id);
        Directory.CreateDirectory(dir);
        Directory.CreateDirectory(Path.Combine(dir, "files"));
        WriteRoomJson(info);
        File.WriteAllText(GetMessagesPath(id), string.Empty, Encoding.UTF8);

        _rooms[id] = new RoomRuntime(info);
        return info;
    }

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

            _rooms[info.Id] = new RoomRuntime(info);
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
        EnsureRuntime(roomId);
        lock (_ioLock)
        {
            var all = ReadAllMessages(roomId);
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

            var line = JsonSerializer.Serialize(message, JsonlOptions);
            File.AppendAllText(GetMessagesPath(roomId), line + Environment.NewLine, Encoding.UTF8);
            runtime.RaiseMessage(message);
            return message;
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

        if (declaredLength is > 0 && declaredLength != size)
        {
            // tolerate mismatch; trust written size
        }

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

        var metaPath = Path.Combine(dir, "meta.json");
        File.WriteAllText(metaPath, JsonSerializer.Serialize(info, JsonOptions), Encoding.UTF8);
        return info;
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

    public IDisposable Subscribe(string roomId, Action<RoomMessage> handler)
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
        return _rooms.GetOrAdd(info.Id, _ => new RoomRuntime(info));
    }

    private List<RoomMessage> ReadAllMessages(string roomId)
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
            _ => "application/octet-stream"
        };
    }

    private sealed class RoomRuntime
    {
        private readonly object _gate = new();
        private readonly List<Action<RoomMessage>> _handlers = new();

        public RoomRuntime(RoomInfo info)
        {
            Info = info;
        }

        public RoomInfo Info { get; private set; }

        public void UpdateInfo(RoomInfo info) => Info = info;

        public IDisposable Subscribe(Action<RoomMessage> handler)
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

        public void RaiseMessage(RoomMessage message)
        {
            Action<RoomMessage>[] snapshot;
            lock (_gate)
            {
                snapshot = _handlers.ToArray();
            }

            foreach (var handler in snapshot)
            {
                try
                {
                    handler(message);
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
