using System.Text;

namespace Athlon.Agent.Core.Plan;

public enum PlanSubmitIntent
{
    Build,
    Revise,
    Ask
}

public enum PlanBuildChoice
{
    Build,
    Revise
}

/// <summary>
/// Classifies composer text while a plan is waiting for confirmation.
/// Clear start phrases build; substantive text revises; short or ambiguous text asks.
/// </summary>
public static class PlanBuildIntent
{
    public const string ChoiceRequestId = "plan-build-intent";
    public const string BuildLabelZh = "开始实现";
    public const string BuildLabelEn = "Start implementing";
    public const string ReviseLabelZh = "继续修改计划";
    public const string ReviseLabelEn = "Keep revising";

    private static readonly string[] BuildPhrases =
    [
        "开始实现",
        "按这个做",
        "开始做",
        "implement this",
        "start building",
        "start implementing",
        "implement the plan",
        "开始构建",
        "按计划实现",
        "按计划做",
        "开工"
    ];

    private static readonly string[] AmbiguousPhrases =
    [
        "好",
        "好的",
        "好吧",
        "可以",
        "行",
        "继续",
        "嗯",
        "嗯嗯",
        "对",
        "是",
        "是的",
        "ok",
        "okay",
        "yes",
        "y",
        "k",
        "go",
        "continue",
        "sure",
        "yep",
        "yeah"
    ];

    public static PlanSubmitIntent Classify(string? text)
    {
        var normalized = Normalize(text);
        if (normalized.Length == 0 || IsAmbiguous(normalized))
        {
            return PlanSubmitIntent.Ask;
        }

        if (IsBuild(normalized))
        {
            return PlanSubmitIntent.Build;
        }

        return PlanSubmitIntent.Revise;
    }

    public static UserQuestion CreateQuestion(bool english) => new()
    {
        RequestId = ChoiceRequestId,
        AllowFreeText = false,
        Questions =
        [
            new UserQuestionItem
            {
                Id = "intent",
                Prompt = english
                    ? "Start implementing this plan, or keep revising it?"
                    : "开始实现这个计划，还是继续修改？",
                Options =
                [
                    new UserQuestionOption
                    {
                        Id = "build",
                        Label = english ? BuildLabelEn : BuildLabelZh
                    },
                    new UserQuestionOption
                    {
                        Id = "revise",
                        Label = english ? ReviseLabelEn : ReviseLabelZh
                    }
                ]
            }
        ]
    };

    public static bool TryParseChoice(string? text, out PlanBuildChoice choice)
    {
        choice = default;
        if (string.IsNullOrWhiteSpace(text)
            || !text.Contains("Clarification answers:", StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var raw in text.Split('\n'))
        {
            var colon = raw.LastIndexOf(':');
            if (colon < 0 || colon >= raw.Length - 1)
            {
                continue;
            }

            var answer = Normalize(raw[(colon + 1)..]);
            if (IsLabel(answer, BuildLabelZh, BuildLabelEn))
            {
                choice = PlanBuildChoice.Build;
                return true;
            }

            if (IsLabel(answer, ReviseLabelZh, ReviseLabelEn))
            {
                choice = PlanBuildChoice.Revise;
                return true;
            }
        }

        return false;
    }

    private static bool IsBuild(string normalized)
    {
        foreach (var phrase in BuildPhrases)
        {
            if (string.Equals(normalized, phrase, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsAmbiguous(string normalized)
    {
        foreach (var phrase in AmbiguousPhrases)
        {
            if (string.Equals(normalized, phrase, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        if (normalized.Length <= 2)
        {
            return true;
        }

        var words = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.Length == 1
            && words[0].Length <= 3
            && normalized.All(ch => ch < 128);
    }

    private static bool IsLabel(string answer, string chinese, string english) =>
        string.Equals(answer, chinese, StringComparison.Ordinal)
        || string.Equals(answer, english, StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(text.Length);
        var pendingSpace = false;
        foreach (var ch in text.Trim())
        {
            if (char.IsWhiteSpace(ch))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(ch);
        }

        var value = builder.ToString().Trim().TrimEnd('.', '!', '?', '。', '！', '？', '~', '～');
        while (value.Length > 0 && value[^1] is '吧' or '了' or '啊' or '呀' or '呢')
        {
            value = value[..^1].TrimEnd();
        }

        if (value.EndsWith(" please", StringComparison.OrdinalIgnoreCase))
        {
            value = value[..^" please".Length].TrimEnd();
        }

        return value.Trim();
    }
}
