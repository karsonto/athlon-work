using System.IO;

namespace Athlon.Agent.Tests;

/// <summary>
/// Contract: default turn-end path must not unconditionally SyncChatView(authoritative).
/// </summary>
public sealed class TurnEndReplayGuardTests
{
    [Fact]
    public void FinalizeTurn_gates_authoritative_replay_behind_ChatRenderTurnEndReplay()
    {
        var source = LocateTurnLifecycleSource();
        Assert.Contains("if (ChatRenderTurnEndReplay)", source, StringComparison.Ordinal);
        Assert.Contains("SyncChatView(immediate: true, authoritative: true);", source, StringComparison.Ordinal);

        // The authoritative sync must appear only inside the gated block, not as an
        // unconditional call after SealActivitySegment.
        var sealIdx = source.IndexOf("SealActivitySegment();", StringComparison.Ordinal);
        Assert.True(sealIdx >= 0);
        var afterSeal = source[sealIdx..];
        var gateIdx = afterSeal.IndexOf("if (ChatRenderTurnEndReplay)", StringComparison.Ordinal);
        var syncIdx = afterSeal.IndexOf("SyncChatView(immediate: true, authoritative: true);", StringComparison.Ordinal);
        Assert.True(gateIdx >= 0 && syncIdx > gateIdx);
    }

    private static string LocateTurnLifecycleSource()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(
                dir.FullName,
                "src",
                "Athlon.Agent.App",
                "Services",
                "SessionTurnUiController.TurnLifecycle.cs");
            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate);
            }
        }

        throw new FileNotFoundException("SessionTurnUiController.TurnLifecycle.cs");
    }
}
