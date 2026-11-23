namespace Athlon.Agent.App.Services.Room;

public sealed record RoomAgentTurnOptions(string RoomId);

/// <summary>Ambient scope so room-only tools know the active room.</summary>
public sealed class RoomAgentTurnScope : IDisposable
{
    private static readonly AsyncLocal<RoomAgentTurnScope?> Ambient = new();
    private readonly RoomAgentTurnScope? _previous;

    private RoomAgentTurnScope(RoomAgentTurnOptions options)
    {
        _previous = Ambient.Value;
        Options = options;
        Ambient.Value = this;
    }

    public RoomAgentTurnOptions Options { get; }

    public static RoomAgentTurnScope? Current => Ambient.Value;

    public static IDisposable Enter(RoomAgentTurnOptions options) => new RoomAgentTurnScope(options);

    public void Dispose() => Ambient.Value = _previous;
}
