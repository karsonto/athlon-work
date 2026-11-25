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
    public void UpdateMessageText_RaisesMessageUpdated()
    {
        using var temp = new TempDirectoryScope("athlon-room-upd");
        var store = new RoomStore(new TempPathProvider(temp.Root));
        var room = store.CreateRoom();
        RoomStoreEvent? seen = null;
        using var _ = store.SubscribeEvents(room.Id, ev => seen = ev);

        var msg = store.AppendMessage(room.Id, new RoomMessage
        {
            SenderId = RoomAgentIds.SenderId,
            DisplayName = RoomAgentIds.DisplayName,
            Kind = "agent",
            Text = "…"
        });
        Assert.Equal(RoomStoreEventKind.Message, seen!.Value.Kind);

        var updated = store.UpdateMessageText(room.Id, msg.Id, "hello agent");
        Assert.NotNull(updated);
        Assert.Equal("hello agent", updated!.Text);
        Assert.Equal(RoomStoreEventKind.MessageUpdated, seen.Value.Kind);
        Assert.Equal("hello agent", store.GetMessages(room.Id).Single().Text);
    }

    [Fact]
    public void SaveAttachment_SanitizesPathEscape()
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
        Assert.StartsWith(Path.Combine(temp.Root, "rooms", room.Id), path!);

        Assert.Null(store.GetAttachmentFilePath(room.Id, "../" + meta.Id));
        Assert.Null(store.GetAttachmentMeta(room.Id, "not-a-valid!"));
    }

    [Fact]
    public void SaveAttachment_WithWorkspace_WritesOnlyUnderAthlonRoom()
    {
        using var temp = new TempDirectoryScope("athlon-room-ws");
        var store = new RoomStore(new TempPathProvider(temp.Root));
        var workspace = Path.Combine(temp.Root, "proj");
        Directory.CreateDirectory(workspace);
        var room = store.CreateRoom(workspaceRoot: workspace);

        using var ok = new MemoryStream(Encoding.UTF8.GetBytes("only-once"));
        var meta = store.SaveAttachment(room.Id, "shot.png", "image/png", ok, workspaceRoot: workspace);

        Assert.NotNull(meta.WorkspacePath);
        Assert.True(File.Exists(meta.WorkspacePath));
        Assert.Contains(Path.Combine(".athlon-room", room.Id), meta.WorkspacePath!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("only-once", File.ReadAllText(meta.WorkspacePath!));

        var storeBlob = Path.Combine(temp.Root, "rooms", room.Id, "files", meta.Id, meta.StoredName);
        Assert.False(File.Exists(storeBlob));
        Assert.Equal(meta.WorkspacePath, store.GetAttachmentFilePath(room.Id, meta.Id));
    }

    [Fact]
    public void SaveAttachmentFromPath_PostsFileMessageLikeShareTool()
    {
        using var temp = new TempDirectoryScope("athlon-room-share");
        var store = new RoomStore(new TempPathProvider(temp.Root));
        var workspace = Path.Combine(temp.Root, "ws");
        Directory.CreateDirectory(workspace);
        var room = store.CreateRoom(workspaceRoot: workspace);
        var docx = Path.Combine(workspace, "notes.docx");
        File.WriteAllBytes(docx, Encoding.UTF8.GetBytes("PK fake-docx"));

        var attachment = store.SaveAttachmentFromPath(room.Id, docx, workspace);
        Assert.Equal("notes.docx", attachment.FileName);
        Assert.False(attachment.IsImage);
        Assert.Equal(Path.GetFullPath(docx), attachment.WorkspacePath);
        Assert.False(File.Exists(Path.Combine(temp.Root, "rooms", room.Id, "files", attachment.Id, "notes.docx")));

        store.AppendMessage(room.Id, new RoomMessage
        {
            SenderId = RoomAgentIds.SenderId,
            DisplayName = RoomAgentIds.DisplayName,
            Kind = "file",
            Text = "已分享文档",
            Attachments = [attachment]
        });

        var last = store.GetMessages(room.Id).Last();
        Assert.Equal("file", last.Kind);
        Assert.Single(last.Attachments);
        Assert.Equal(Path.GetFullPath(docx), store.GetAttachmentFilePath(room.Id, last.Attachments[0].Id));
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
