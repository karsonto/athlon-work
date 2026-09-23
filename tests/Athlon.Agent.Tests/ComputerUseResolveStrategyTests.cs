using Athlon.Agent.Core.ComputerUse;

namespace Athlon.Agent.Tests;

/// <summary>
/// Covers the pure resolution rules that route <c>computer_interact</c> to a channel. The host
/// itself has no unit tests, so these rules must stay pure and covered here.
/// </summary>
public sealed class ComputerUseResolveStrategyTests
{
    [Fact]
    public void Resolve_ElementNative_OnlyWhenElementSuppliedWithoutPixels()
    {
        var strategy = ComputerUseResolveStrategyClassifier.Resolve(
            action: "click",
            hasElementId: true,
            hasImagePoint: false,
            hasPhysicalPoint: false);

        Assert.Equal(ComputerUseResolveStrategy.ElementNative, strategy);
    }

    [Fact]
    public void Resolve_ImagePoint_WinsOverElementId()
    {
        // Explicit screenshot pixels are a precise visual instruction; a coarse element from a
        // shallow tree must never silently override them.
        var strategy = ComputerUseResolveStrategyClassifier.Resolve(
            action: "click",
            hasElementId: true,
            hasImagePoint: true,
            hasPhysicalPoint: false);

        Assert.Equal(ComputerUseResolveStrategy.ImagePoint, strategy);
    }

    [Theory]
    [InlineData("right_click")]
    [InlineData("double_click")]
    [InlineData("drag")]
    public void Resolve_CoordinateOnlyActions_NeverGoNative(string action)
    {
        // Invoke's double-click semantics depend on the control, right-click menus are coordinate
        // sensitive, and UI Automation has no generic drag pattern.
        var strategy = ComputerUseResolveStrategyClassifier.Resolve(
            action: action,
            hasElementId: true,
            hasImagePoint: false,
            hasPhysicalPoint: false);

        Assert.Equal(ComputerUseResolveStrategy.ElementPoint, strategy);
    }

    [Fact]
    public void Resolve_ElementIdWithoutNativeForm_FallsBackToElementPoint()
    {
        var strategy = ComputerUseResolveStrategyClassifier.Resolve(
            action: "double_click",
            hasElementId: true,
            hasImagePoint: false,
            hasPhysicalPoint: false);

        Assert.NotEqual(ComputerUseResolveStrategy.ElementNative, strategy);
        Assert.Equal(ComputerUseResolveStrategy.ElementPoint, strategy);
    }

    [Fact]
    public void Resolve_ElementIdBeatsPhysicalPoint()
    {
        var strategy = ComputerUseResolveStrategyClassifier.Resolve(
            action: "click",
            hasElementId: true,
            hasImagePoint: false,
            hasPhysicalPoint: true);

        Assert.Equal(ComputerUseResolveStrategy.ElementNative, strategy);
    }

    [Fact]
    public void Resolve_PhysicalPoint_UsedWhenNoElementOrPixels()
    {
        var strategy = ComputerUseResolveStrategyClassifier.Resolve(
            action: "click",
            hasElementId: false,
            hasImagePoint: false,
            hasPhysicalPoint: true);

        Assert.Equal(ComputerUseResolveStrategy.PhysicalPoint, strategy);
    }

    [Fact]
    public void Resolve_NoTarget_ReturnsNone()
    {
        var strategy = ComputerUseResolveStrategyClassifier.Resolve(
            action: "click",
            hasElementId: false,
            hasImagePoint: false,
            hasPhysicalPoint: false);

        Assert.Equal(ComputerUseResolveStrategy.None, strategy);
    }

    [Fact]
    public void Classify_KeepsHistoricalPointerOnlyRouting()
    {
        // Classify is the pre-Phase-3 view used for telemetry comparison: with an element id and no
        // pixels it reports ElementPoint, never ElementNative.
        var strategy = ComputerUseResolveStrategyClassifier.Classify(
            isPointerAction: true,
            hasElementId: true,
            hasImagePoint: false,
            hasPhysicalPoint: false);

        Assert.Equal(ComputerUseResolveStrategy.ElementPoint, strategy);
    }

    [Theory]
    [InlineData(ComputerUseResolveStrategy.ElementNative, "element_native")]
    [InlineData(ComputerUseResolveStrategy.ElementPoint, "element_point")]
    [InlineData(ComputerUseResolveStrategy.ImagePoint, "image_point")]
    [InlineData(ComputerUseResolveStrategy.PhysicalPoint, "physical_point")]
    [InlineData(ComputerUseResolveStrategy.None, "none")]
    public void ToWireValue_IsStable(ComputerUseResolveStrategy strategy, string expected)
    {
        Assert.Equal(expected, ComputerUseResolveStrategyClassifier.ToWireValue(strategy));
    }

    [Theory]
    [InlineData("click", true)]
    [InlineData("double_click", true)]
    [InlineData("right_click", true)]
    [InlineData("drag", true)]
    [InlineData("scroll", true)]
    [InlineData("type_text", false)]
    [InlineData("key", false)]
    [InlineData("hotkey", false)]
    public void IsPointerAction_MatchesDocumentedSet(string action, bool expected)
    {
        Assert.Equal(expected, ComputerUseActionKinds.IsPointerAction(action));
    }

    [Theory]
    [InlineData("ENTER", true)]
    [InlineData("enter", true)]
    [InlineData("SPACE", true)]
    [InlineData("TAB", false)]
    [InlineData("CTRL+S", false)]
    [InlineData("F5", false)]
    public void IsActivationKey_OnlyEnterAndSpace(string key, bool expected)
    {
        Assert.Equal(expected, ComputerUseElementActionResolver.IsActivationKey(key));
    }

    [Fact]
    public void CandidatePatterns_Click_PrefersInvokeThenFallsBack()
    {
        var patterns = ComputerUseElementActionResolver.CandidatePatterns("click", key: null);

        Assert.Equal(ComputerUseElementPattern.Invoke, patterns[0]);
        Assert.Contains(ComputerUseElementPattern.SelectionItem, patterns);
        Assert.Contains(ComputerUseElementPattern.Toggle, patterns);
        Assert.Contains(ComputerUseElementPattern.ExpandCollapse, patterns);
    }

    [Fact]
    public void CandidatePatterns_TypeText_UsesValuePatternOnly()
    {
        var patterns = ComputerUseElementActionResolver.CandidatePatterns("type_text", key: null);

        Assert.Equal([ComputerUseElementPattern.Value], patterns);
    }

    [Theory]
    [InlineData("drag")]
    [InlineData("right_click")]
    [InlineData("double_click")]
    public void CandidatePatterns_CoordinateOnlyActions_AreEmpty(string action)
    {
        Assert.Empty(ComputerUseElementActionResolver.CandidatePatterns(action, key: null));
    }

    [Fact]
    public void CandidatePatterns_NonActivationKey_IsEmpty()
    {
        // TB/CTRL+S must be delivered as real key input: there is no native equivalent.
        Assert.Empty(ComputerUseElementActionResolver.CandidatePatterns("key", "CTRL+S"));
        Assert.Empty(ComputerUseElementActionResolver.CandidatePatterns("hotkey", "F5"));
    }

    [Fact]
    public void CandidatePatterns_ActivationKey_MapsToClickPatterns()
    {
        var patterns = ComputerUseElementActionResolver.CandidatePatterns("key", "ENTER");

        Assert.Equal(ComputerUseElementPattern.Invoke, patterns[0]);
    }

    [Fact]
    public void Plan_TypeText_RequiresWriteVerification()
    {
        var plan = ComputerUseElementActionResolver.Plan(
            "type_text",
            key: null,
            scrollDelta: 0,
            text: "hello");

        Assert.NotNull(plan);
        Assert.Equal(ComputerUseElementPattern.Value, plan!.Pattern);
        Assert.Equal("hello", plan.Value);
        Assert.True(plan.VerifyWrite);
    }

    [Theory]
    [InlineData("drag")]
    [InlineData("right_click")]
    public void Plan_CoordinateOnlyActions_AreNull(string action)
    {
        Assert.Null(ComputerUseElementActionResolver.Plan(action, key: null, scrollDelta: 0, text: null));
    }

    [Fact]
    public void Plan_TypeTextWithoutText_IsNull()
    {
        Assert.Null(ComputerUseElementActionResolver.Plan("type_text", key: null, scrollDelta: 0, text: null));
    }
}
