using Athlon.Agent.Core.Plan;

namespace Athlon.Agent.Tests;

public sealed class PlanBuildIntentTests
{
    [Theory]
    [InlineData("开始实现")]
    [InlineData("按这个做")]
    [InlineData("开始做")]
    [InlineData("implement this")]
    [InlineData("start building")]
    [InlineData("开始实现吧")]
    public void Classify_ExplicitStart_IsBuild(string text)
    {
        Assert.Equal(PlanSubmitIntent.Build, PlanBuildIntent.Classify(text));
    }

    [Theory]
    [InlineData("改第 2 步")]
    [InlineData("把验收改成包含单元测试")]
    [InlineData("please rename the second step")]
    public void Classify_SubstantiveEdit_IsRevise(string text)
    {
        Assert.Equal(PlanSubmitIntent.Revise, PlanBuildIntent.Classify(text));
    }

    [Theory]
    [InlineData("好")]
    [InlineData("继续")]
    [InlineData("ok")]
    [InlineData("yes")]
    public void Classify_ShortAmbiguous_IsAsk(string text)
    {
        Assert.Equal(PlanSubmitIntent.Ask, PlanBuildIntent.Classify(text));
    }

    [Fact]
    public void ChoiceAnswer_Build_RoundTrips()
    {
        var question = PlanBuildIntent.CreateQuestion(english: false);
        var text = UserQuestion.FormatUserAnswer(
            question,
            new Dictionary<string, IReadOnlyList<string>> { ["intent"] = ["build"] },
            freeText: null);

        Assert.True(PlanBuildIntent.TryParseChoice(text, out var choice));
        Assert.Equal(PlanBuildChoice.Build, choice);
    }

    [Fact]
    public void ChoiceAnswer_Revise_RoundTrips()
    {
        var question = PlanBuildIntent.CreateQuestion(english: true);
        var text = UserQuestion.FormatUserAnswer(
            question,
            new Dictionary<string, IReadOnlyList<string>> { ["intent"] = ["revise"] },
            freeText: null);

        Assert.True(PlanBuildIntent.TryParseChoice(text, out var choice));
        Assert.Equal(PlanBuildChoice.Revise, choice);
    }
}
