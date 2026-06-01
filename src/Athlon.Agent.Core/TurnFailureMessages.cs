using System.Text.RegularExpressions;

namespace Athlon.Agent.Core;

public static class TurnFailureMessages
{
    public const string ModelCallFailedPrefix = "模型调用失败：";

    private static readonly Regex QuotedPathRegex = new(
        @"'([^']+)'",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static string FormatModelCallFailure(Exception exception)
    {
        var detail = DescribeException(exception);
        if (detail.StartsWith(ModelCallFailedPrefix, StringComparison.Ordinal))
        {
            return detail;
        }

        return ModelCallFailedPrefix + detail;
    }

    private static string DescribeException(Exception exception)
    {
        if (exception is UnauthorizedAccessException or IOException)
        {
            var path = TryExtractPath(exception);
            if (!string.IsNullOrWhiteSpace(path))
            {
                return $"无法写入或读取「{path}」：{exception.Message}";
            }

            return $"文件访问失败：{exception.Message}";
        }

        return exception.Message;
    }

    private static string? TryExtractPath(Exception exception)
    {
        if (exception is not null && TryExtractPathFromText(exception.Message, out var fromMessage))
        {
            return fromMessage;
        }

        return exception?.InnerException is not null
            ? TryExtractPath(exception.InnerException)
            : null;
    }

    private static bool TryExtractPathFromText(string? text, out string? path)
    {
        path = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var match = QuotedPathRegex.Match(text);
        if (!match.Success)
        {
            return false;
        }

        path = match.Groups[1].Value;
        return !string.IsNullOrWhiteSpace(path);
    }
}
