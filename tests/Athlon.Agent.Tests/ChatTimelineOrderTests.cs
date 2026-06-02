using Athlon.Agent.Core;
using Athlon.Agent.Core.Compaction;

namespace Athlon.Agent.Tests;

public sealed class ChatTimelineOrderTests
{
    [Fact]
    public void OrderForDisplay_PlacesCompactionAfterKeptTailByCreatedAt()
    {
        var tailTime = new DateTimeOffset(2026, 6, 2, 14, 17, 8, TimeSpan.Zero);
        var compactTime = tailTime.AddMinutes(3);

        var tailAssistant = ChatMessage.Create(MessageRole.Assistant, "kept reply") with { CreatedAt = tailTime };
        var compaction = CompactionMessageContent.CreateCompactionMessage(
            CompactionMessageContent.CreateConversationCompact(1000, 500, 10, null, "summary")) with
        {
            CreatedAt = compactTime
        };

        // Storage order after compact: audit, hidden summary, tail (model context).
        var storageOrder = new[]
        {
            compaction,
            SummaryMessageBuilder.CreateSummaryPlaceholder("summary", null) with { CreatedAt = compactTime.AddSeconds(1) },
            tailAssistant
        };

        var displayOrder = ChatTimelineOrder.OrderForDisplay(storageOrder);

        Assert.Equal(tailAssistant.Id, displayOrder[0].Id);
        Assert.Equal(MessageRole.Compaction, displayOrder[1].Role);
    }
}
