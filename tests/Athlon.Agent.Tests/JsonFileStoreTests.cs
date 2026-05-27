using System.Text.Json;
using Athlon.Agent.Core;
using Athlon.Agent.Infrastructure;

namespace Athlon.Agent.Tests;

public sealed class JsonFileStoreTests
{
    [Fact]
    public void Serialize_session_writes_chinese_literals_not_unicode_escapes()
    {
        var session = AgentSession.Create("json-encoding-test")
            .WithMessage(ChatMessage.Create(MessageRole.User, "我是用户"));

        var json = JsonSerializer.Serialize(session, JsonFileStore.Options);

        Assert.Contains("我是用户", json);
        Assert.DoesNotContain(@"\u6211", json);
    }

    [Fact]
    public async Task SaveAsync_round_trips_chinese_content()
    {
        var store = new JsonFileStore();
        var path = Path.Combine(Path.GetTempPath(), $"athlon-json-{Guid.NewGuid():N}.json");
        var session = AgentSession.Create("round-trip")
            .WithMessage(ChatMessage.Create(MessageRole.Assistant, "你好，世界"));

        try
        {
            await store.SaveAsync(path, session);
            var raw = await File.ReadAllTextAsync(path);

            Assert.Contains("你好，世界", raw);
            Assert.DoesNotContain(@"\u4f60", raw);

            var loaded = await store.LoadAsync<AgentSession>(path);
            Assert.NotNull(loaded);
            Assert.Equal("你好，世界", loaded!.Messages[^1].Content);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }
}
