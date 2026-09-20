using System.IO;
using System.Text.Json;
using Athlon.Agent.App.Services;

namespace Athlon.Agent.Tests;

/// <summary>
/// Contract tests for the Phase-4 session DOM-snapshot fast path: the C# switch/invalidate
/// messages and the JS snapshot API they drive. The hit path must reuse already-rendered nodes,
/// so it must never re-run the highlight / Mermaid passes.
/// </summary>
public sealed class SessionSnapshotProtocolTests
{
    [Fact]
    public void SerializeSwitchSessionCommand_carries_session_revision_and_generation()
    {
        var json = ChatEventSerializer.SerializeSwitchSessionCommand("s-1", "rev-9", 42);

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.Equal("switchSession", root.GetProperty("command").GetString());
        Assert.Equal("s-1", root.GetProperty("sessionId").GetString());
        Assert.Equal("rev-9", root.GetProperty("revision").GetString());
        Assert.Equal(42, root.GetProperty("renderGeneration").GetInt32());
    }

    [Fact]
    public void SerializeInvalidateSessionCommand_targets_one_session()
    {
        var json = ChatEventSerializer.SerializeInvalidateSessionCommand("s-2");

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.Equal("invalidateSession", root.GetProperty("command").GetString());
        Assert.Equal("s-2", root.GetProperty("sessionId").GetString());
    }

    [Fact]
    public void SerializeEventsCommand_emits_revision_only_when_asked()
    {
        var withRevision = ChatEventSerializer.SerializeEventsCommand(
            "replay",
            Array.Empty<string>(),
            7,
            replayComplete: true,
            sessionId: "s-1",
            revision: "rev-1");

        using var withDocument = JsonDocument.Parse(withRevision);
        Assert.Equal("rev-1", withDocument.RootElement.GetProperty("revision").GetString());
        Assert.True(withDocument.RootElement.GetProperty("replayComplete").GetBoolean());

        var withoutRevision = ChatEventSerializer.SerializeEventsCommand("append", Array.Empty<string>(), 7);
        using var withoutDocument = JsonDocument.Parse(withoutRevision);
        Assert.False(withoutDocument.RootElement.TryGetProperty("revision", out _));
    }

    [Fact]
    public void Timeline_exposes_the_per_session_snapshot_api()
    {
        var state = ReadChatAsset("timeline-state.js");

        Assert.Contains("var sessionSnapshots = new Map();", state, StringComparison.Ordinal);
        Assert.Contains("function saveSessionSnapshot(", state, StringComparison.Ordinal);
        Assert.Contains("function restoreSessionSnapshot(", state, StringComparison.Ordinal);
        Assert.Contains("function switchSessionSnapshot(", state, StringComparison.Ordinal);
        Assert.Contains("function invalidateSessionSnapshot(", state, StringComparison.Ordinal);
        // Restoring is revision-gated: any content change must fall back to the replay.
        Assert.Contains(
            "if (revision && revision !== entry.revision) return false;",
            state,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Restore_path_does_not_rerun_highlight_or_mermaid()
    {
        var state = ReadChatAsset("timeline-state.js");
        var restore = ExtractFunction(state, "function restoreSessionSnapshot(");

        Assert.DoesNotContain("enhanceCodeBlocks(", restore, StringComparison.Ordinal);
        Assert.DoesNotContain("renderMermaidBlocks(", restore, StringComparison.Ordinal);
        // ...but the lazy highlighter observer is re-armed for restored, not-yet-highlighted blocks.
        Assert.Contains("codeObserver.observe(", restore, StringComparison.Ordinal);
    }

    [Fact]
    public void Protocol_handles_switch_and_invalidate_and_reports_the_outcome()
    {
        var protocol = ReadChatAsset("timeline-protocol.js");

        Assert.Contains("command.command === 'switchSession'", protocol, StringComparison.Ordinal);
        Assert.Contains("command.command === 'invalidateSession'", protocol, StringComparison.Ordinal);
        Assert.Contains("'snapshotRestored' : 'snapshotMiss'", protocol, StringComparison.Ordinal);
        // The revision the page rendered from is remembered, and only then can a switch hit.
        Assert.Contains("state.renderedRevision = command.revision || null;", protocol, StringComparison.Ordinal);
    }

    [Fact]
    public void Reset_only_invalidates_the_mounted_session()
    {
        var render = ReadChatAsset("timeline-render.js");
        var reset = ExtractFunction(render, "function resetTimeline(");

        Assert.Contains("invalidateSessionSnapshot(state.currentSessionId);", reset, StringComparison.Ordinal);
        Assert.DoesNotContain("sessionSnapshots.clear()", reset, StringComparison.Ordinal);
    }

    private static string ExtractFunction(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Missing function: {signature}");

        var depth = 0;
        for (var i = start; i < source.Length; i++)
        {
            if (source[i] == '{')
            {
                depth++;
            }
            else if (source[i] == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return source[start..(i + 1)];
                }
            }
        }

        return source[start..];
    }

    private static string ReadChatAsset(string fileName)
    {
        var fromBase = Path.Combine(AppContext.BaseDirectory, "Assets", "Chat", fileName);
        if (File.Exists(fromBase))
        {
            return File.ReadAllText(fromBase);
        }

        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "src", "Athlon.Agent.App", "Assets", "Chat", fileName);
            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate);
            }
        }

        throw new FileNotFoundException($"Chat asset not found: {fileName}");
    }
}
