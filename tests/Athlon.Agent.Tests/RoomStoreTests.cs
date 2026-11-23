using System.Text;
using Athlon.Agent.App.Services.Room;
using Athlon.Agent.Infrastructure;

namespace Athlon.Agent.Tests;

public sealed class RoomStoreTests
{
    [Fact]
    public void ValidateToken_AcceptsMatchingToken_RejectsWrong()
    {
        using var temp = new TempDirectoryScope("athlon-room");
        var store = new RoomStore(new TempPathProvider(temp.Root));
        var room = store.CreateRoom("test");

        Assert.True(store.ValidateToken(room.Id, room.Token));
        Assert.False(store.ValidateToken(room.Id, "deadbeef"));
        Assert.False(store.ValidateToken(room.Id, null));
        Assert.False(store.ValidateToken(room.Id, room.Token + "x"));
    }

    [Fact]
    public void SanitizeFileName_StripsTraversal()
    {
        Assert.Equal("secret.txt", RoomStore.SanitizeFileName("../../secret.txt"));
        Assert.Equal("file.bin", RoomStore.SanitizeFileName(".."));
        Assert.Equal("b.txt", RoomStore.SanitizeFileName("a/b.txt"));
    }

    [Fact]
    public void IsSafeId_RejectsTraversalAndOddChars()
    {
        Assert.True(RoomStore.IsSafeId("abc123"));
        Assert.False(RoomStore.IsSafeId("../x"));
        Assert.False(RoomStore.IsSafeId("a/b"));
        Assert.False(RoomStore.IsSafeId(""));
        Assert.False(RoomStore.IsSafeId(new string('a', 100)));
    }

    [Fact]
    public void AppendMessage_PersistsAndReadsAfter()
    {
        using var temp = new TempDirectoryScope("athlon-room-msg");
        var store = new RoomStore(new TempPathProvider(temp.Root));
        var room = store.CreateRoom();

        var first = store.AppendMessage(room.Id, new RoomMessage
        {
            SenderId = "s1",
            DisplayName = "Alice",
            Kind = "text",
            Text = "hello"
        });
        var second = store.AppendMessage(room.Id, new RoomMessage
        {
            SenderId = "s2",
            DisplayName = "Bob",
            Kind = "text",
            Text = "hi"
        });

        var all = store.GetMessages(room.Id);
        Assert.Equal(2, all.Count);
        Assert.Equal("hello", all[0].Text);

        var after = store.GetMessages(room.Id, first.Id);
        Assert.Single(after);
        Assert.Equal(second.Id, after[0].Id);
    }

    [Fact]
    public void SaveAttachment_RejectsOversizeAndPathEscape()
    {
        using var temp = new TempDirectoryScope("athlon-room-att");
        var store = new RoomStore(new TempPathProvider(temp.Root));
        var room = store.CreateRoom();

        using var ok = new MemoryStream(Encoding.UTF8.GetBytes("tiny"));
        var meta = store.SaveAttachment(room.Id, "../../evil.png", "image/png", ok);
        Assert.Equal("evil.png", meta.FileName);
        Assert.True(meta.IsImage);

        var path = store.GetAttachmentFilePath(room.Id, meta.Id);
        Assert.NotNull(path);
        Assert.True(path!.StartsWith(Path.Combine(temp.Root, "rooms", room.Id), StringComparison.OrdinalIgnoreCase));

        Assert.Null(store.GetAttachmentFilePath(room.Id, "../" + meta.Id));
        Assert.Null(store.GetAttachmentMeta(room.Id, "not-a-valid!"));
    }

    private sealed class TempPathProvider(string root) : IAppPathProvider
    {
        public string RootPath { get; } = root;
        public string ConfigPath => Path.Combine(RootPath, "config");
        public string SessionsPath => Path.Combine(RootPath, "sessions");
        public string AuditPath => Path.Combine(RootPath, "audit");
        public string LogsPath => Path.Combine(RootPath, "logs");
        public string CredentialsPath => Path.Combine(RootPath, "credentials");
        public string SkillsPath => Path.Combine(RootPath, "skills");

        public void EnsureCreated()
        {
            Directory.CreateDirectory(RootPath);
        }

        public string ResolveSkillPath(string path) => path;
    }
}
