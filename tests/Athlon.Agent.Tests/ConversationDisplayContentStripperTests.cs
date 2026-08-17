using Athlon.Agent.Core;
using Athlon.Agent.Infrastructure;

namespace Athlon.Agent.Tests;

public sealed class ConversationDisplayContentStripperTests
{
    [Fact]
    public void StripToolContentForDisplay_keeps_file_edit_diff()
    {
        var content = string.Join(
            '\n',
            "ToolCallId: call-1",
            "Tool `file_edit` succeeded.",
            "",
            "Arguments: path = server.ts",
            "Summary: Edited",
            "",
            "--- a/server.ts",
            "+++ b/server.ts",
            "@@ -1,1 +1,1 @@",
            "-a",
            "+b");
        var message = ChatMessage.Create(MessageRole.Tool, content);

        var stripped = ConversationDisplayContentStripper.StripToolContentForDisplay(message);

        Assert.Contains("--- a/server.ts", stripped.Content, StringComparison.Ordinal);
        Assert.Contains("+b", stripped.Content, StringComparison.Ordinal);
    }

    [Fact]
    public void StripToolContentForDisplay_keeps_apply_patch_body()
    {
        var content = string.Join(
            '\n',
            "Tool `apply_patch` succeeded.",
            "Summary: Patched",
            "",
            "*** Begin Patch",
            "*** Update File: src/App.tsx",
            "+export const x = 1;",
            "*** End Patch");
        var message = ChatMessage.Create(MessageRole.Tool, content);

        var stripped = ConversationDisplayContentStripper.StripToolContentForDisplay(message);

        Assert.Contains("*** Begin Patch", stripped.Content, StringComparison.Ordinal);
        Assert.Contains("src/App.tsx", stripped.Content, StringComparison.Ordinal);
    }

    [Fact]
    public void StripToolContentForDisplay_trims_other_tool_bodies()
    {
        var content = string.Join(
            '\n',
            "ToolCallId: call-2",
            "Tool `file_read` succeeded.",
            "",
            "Arguments: path = huge.log",
            "Summary: Read huge.log",
            "",
            "1|line one",
            "2|line two",
            "3|line three");
        var message = ChatMessage.Create(MessageRole.Tool, content);

        var stripped = ConversationDisplayContentStripper.StripToolContentForDisplay(message);

        Assert.Contains("Summary: Read huge.log", stripped.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("line one", stripped.Content, StringComparison.Ordinal);
    }
}
