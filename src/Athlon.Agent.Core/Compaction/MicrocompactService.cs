namespace Athlon.Agent.Core.Compaction;

public sealed class MicrocompactService
{
    public const string ClearedContent = "[cleared]";

    public void Apply(IList<ChatMessage> messages, int keepToolMessages, int minContentLength = 100)
    {
        var toolIndices = new List<int>();
        for (var i = 0; i < messages.Count; i++)
        {
            var message = messages[i];
            if (message.Role == MessageRole.Tool && message.Content.Length > minContentLength)
            {
                toolIndices.Add(i);
            }
        }

        if (toolIndices.Count <= keepToolMessages)
        {
            return;
        }

        var clearCount = toolIndices.Count - keepToolMessages;
        for (var i = 0; i < clearCount; i++)
        {
            var index = toolIndices[i];
            var message = messages[index];
            if (message.Content.Length > minContentLength)
            {
                messages[index] = message with { Content = ClearedContent };
            }
        }
    }
}
