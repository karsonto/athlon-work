namespace Athlon.Agent.Infrastructure;

/// <summary>
/// Suggests nearby file names in the same directory when a read target does not exist.
/// </summary>
internal static class SimilarFileNameSuggester
{
    public const int MaxSuggestions = 3;

    public static IReadOnlyList<string> Suggest(string requestedFileName, IEnumerable<string> candidateNames)
    {
        var requested = requestedFileName.Trim();
        if (requested.Length == 0)
        {
            return [];
        }

        var maxDistance = requested.Length <= 2
            ? 1
            : Math.Max(3, requested.Length / 2);
        var ranked = new List<(string Name, int Distance)>();
        foreach (var candidate in candidateNames)
        {
            if (string.IsNullOrWhiteSpace(candidate) || candidate is "." or "..")
            {
                continue;
            }

            if (string.Equals(candidate, requested, StringComparison.Ordinal))
            {
                continue;
            }

            var distance = EditDistance(
                requested.ToLowerInvariant(),
                candidate.ToLowerInvariant(),
                maxDistance);
            if (distance is null)
            {
                continue;
            }

            ranked.Add((candidate, distance.Value));
        }

        return ranked
            .OrderBy(item => item.Distance)
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .Take(MaxSuggestions)
            .Select(item => item.Name)
            .ToArray();
    }

    public static string FormatNotFound(IReadOnlyList<string> suggestions) =>
        suggestions.Count == 0
            ? "File not found"
            : "File not found. Similar names: " + string.Join(", ", suggestions);

    private static int? EditDistance(string left, string right, int maxDistance)
    {
        if (Math.Abs(left.Length - right.Length) > maxDistance)
        {
            return null;
        }

        var previous = new int[right.Length + 1];
        var current = new int[right.Length + 1];
        for (var column = 0; column <= right.Length; column++)
        {
            previous[column] = column;
        }

        for (var row = 1; row <= left.Length; row++)
        {
            current[0] = row;
            var rowBest = current[0];
            for (var column = 1; column <= right.Length; column++)
            {
                var cost = left[row - 1] == right[column - 1] ? 0 : 1;
                current[column] = Math.Min(
                    Math.Min(current[column - 1] + 1, previous[column] + 1),
                    previous[column - 1] + cost);
                if (current[column] < rowBest)
                {
                    rowBest = current[column];
                }
            }

            if (rowBest > maxDistance)
            {
                return null;
            }

            (previous, current) = (current, previous);
        }

        return previous[right.Length] <= maxDistance ? previous[right.Length] : null;
    }
}
