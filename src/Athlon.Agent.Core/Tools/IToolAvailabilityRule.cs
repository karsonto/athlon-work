namespace Athlon.Agent.Core.Tools;

/// <summary>
/// Ordered local-tool enablement rule. First matching rule that returns a non-null
/// decision wins; otherwise the tool is allowed.
/// </summary>
public interface IToolAvailabilityRule
{
    string Name { get; }

    bool Applies(ToolAvailabilityContext context);

    /// <returns>true/false to decide, or null to defer to later rules.</returns>
    bool? Decide(ToolAvailabilityContext context, ToolFacet facets);
}
