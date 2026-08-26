using System.Text;
using System.Text.RegularExpressions;

namespace Athlon.Agent.Infrastructure.Knowledge;

public static partial class KnowledgeOcrResponseParser
{
    [GeneratedRegex(@"^###\s*Page\s+(\d+)\s*$", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex PageHeaderRegex();

    public static IReadOnlyDictionary<int, string> Parse(string response, IReadOnlyList<int> expectedPageNumbers)
    {
        var result = new Dictionary<int, string>();
        if (string.IsNullOrWhiteSpace(response) || expectedPageNumbers.Count == 0)
        {
            return result;
        }

        var matches = PageHeaderRegex().Matches(response);
        if (matches.Count == 0)
        {
            // Single-page fallback: entire response maps to the only expected page.
            if (expectedPageNumbers.Count == 1)
            {
                result[expectedPageNumbers[0]] = response.Trim();
            }

            return result;
        }

        for (var i = 0; i < matches.Count; i++)
        {
            if (!int.TryParse(matches[i].Groups[1].Value, out var pageNumber))
            {
                continue;
            }

            var start = matches[i].Index + matches[i].Length;
            var end = i + 1 < matches.Count ? matches[i + 1].Index : response.Length;
            if (end < start)
            {
                continue;
            }

            var text = response[start..end].Trim();
            if (text.Length > 0)
            {
                result[pageNumber] = text;
            }
        }

        return result;
    }

    public static string BuildUserPrompt(IReadOnlyList<int> pageNumbers)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Extract all readable text from the attached image(s) for knowledge indexing.");
        builder.AppendLine("Rules:");
        builder.AppendLine("- Output plain text only (no chat, no apologies).");
        builder.AppendLine("- Preserve reading order; keep numbers and table/chart labels.");
        builder.AppendLine("- Images may be cropped figures from a PDF page, not full pages.");
        builder.AppendLine("- For each image use exactly this header then the transcribed text:");
        builder.AppendLine("### Page N");
        builder.Append("Images in order (N values): ");
        builder.Append(string.Join(", ", pageNumbers));
        builder.AppendLine(".");
        return builder.ToString();
    }
}
