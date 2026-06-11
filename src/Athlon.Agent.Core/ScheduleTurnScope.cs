namespace Athlon.Agent.Core;

public sealed record ScheduleTurnOptions(
    string? ModelNameOverride = null,
    bool AllowToolCalls = true,
    int? MaxModelToolRounds = null);

public sealed class ScheduleTurnScope : IDisposable
{
    private static readonly AsyncLocal<ScheduleTurnScope?> Ambient = new();

    private readonly ScheduleTurnScope? _previous;

    private ScheduleTurnScope(ScheduleTurnOptions options)
    {
        _previous = Ambient.Value;
        ModelNameOverride = options.ModelNameOverride;
        AllowToolCalls = options.AllowToolCalls;
        MaxModelToolRounds = options.MaxModelToolRounds;
        Ambient.Value = this;
    }

    public static ScheduleTurnScope? Current => Ambient.Value;

    public string? ModelNameOverride { get; }
    public bool AllowToolCalls { get; }
    public int? MaxModelToolRounds { get; }

    public static IDisposable Enter(ScheduleTurnOptions options) => new ScheduleTurnScope(options);

    public void Dispose() => Ambient.Value = _previous;
}
