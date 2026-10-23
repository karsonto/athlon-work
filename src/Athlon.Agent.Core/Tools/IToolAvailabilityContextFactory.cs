namespace Athlon.Agent.Core.Tools;

/// <summary>Builds the per-turn snapshot used by <see cref="ToolAvailabilityPolicy"/>.</summary>
public interface IToolAvailabilityContextFactory
{
    ToolAvailabilityContext Create();

    /// <summary>Stamp for invalidating cached local tool routers when availability inputs change.</summary>
    string ComputeLocalStamp();
}
