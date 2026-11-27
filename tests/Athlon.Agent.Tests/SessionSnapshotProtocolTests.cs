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

    [Fact]
    public void Atomic_replay_builds_offscreen_and_commits_with_replaceChildren()
    {
        var state = ReadChatAsset("timeline-state.js");
        var render = ReadChatAsset("timeline-render.js");
        var protocol = ReadChatAsset("timeline-protocol.js");

        Assert.Contains("function isAtomicReplayEnabled(", state, StringComparison.Ordinal);
        Assert.Contains("atomicReplay: null", state, StringComparison.Ordinal);
        Assert.Contains("function resetTimelineStateOnly(", render, StringComparison.Ordinal);
        Assert.Contains("if (state.atomicReplay)", render, StringComparison.Ordinal);
        Assert.Contains("function replayEventsAtomic(", protocol, StringComparison.Ordinal);
        Assert.Contains("function beginAtomicReplay(", protocol, StringComparison.Ordinal);
        Assert.Contains("function commitAtomicReplay(", protocol, StringComparison.Ordinal);
        Assert.Contains("function abandonAtomicReplay(", protocol, StringComparison.Ordinal);
        Assert.Contains("root.replaceChildren.apply", protocol, StringComparison.Ordinal);
        Assert.Contains("isAtomicReplayEnabled()", protocol, StringComparison.Ordinal);
        // Legacy path remains for the Ui.ChatRenderAtomicReplay=false rollback.
        Assert.Contains("function replayEvents(", protocol, StringComparison.Ordinal);
        Assert.Contains("root.innerHTML = '';", render, StringComparison.Ordinal);
    }

    [Fact]
    public void Stream_text_reveals_lines_with_blur_fade_and_crossfades_to_markdown()
    {
        var state = ReadChatAsset("timeline-state.js");
        var protocol = ReadChatAsset("timeline-protocol.js");
        var css = ReadChatAsset("chat-shell.css");

        Assert.Contains("case 'ASSISTANT_STREAM_TEXT':", protocol, StringComparison.Ordinal);
        Assert.Contains("function applyAssistantStreamText(", state, StringComparison.Ordinal);
        Assert.Contains("function splitStreamVisualLines(", state, StringComparison.Ordinal);
        Assert.Contains("function crossfadeStreamLinesToMarkdown(", state, StringComparison.Ordinal);
        Assert.Contains("stream-line-enter", state, StringComparison.Ordinal);
        Assert.Contains("prefers-reduced-motion", state, StringComparison.Ordinal);
        // New complete lines must insert before the blur pending tail (not append after it).
        Assert.Contains("host.insertBefore(line, pendingEl)", state, StringComparison.Ordinal);
        // JSON null seq must not skip nextLiveContentSeq.
        Assert.Contains("typeof seq === 'number' && isFinite(seq)", state, StringComparison.Ordinal);
        Assert.Contains("@keyframes streamLineReveal", css, StringComparison.Ordinal);
        Assert.Contains(".stream-line.is-pending", css, StringComparison.Ordinal);
        Assert.Contains(".stream-lines-exit", css, StringComparison.Ordinal);
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
