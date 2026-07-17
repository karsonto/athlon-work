namespace Athlon.Agent.Core;

/// <summary>Unix-style remote path helpers (no Windows Path.GetFullPath).</summary>
public static class RemotePathNormalizer
{
    public static string NormalizeRoot(string root)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            return "/";
        }

        var normalized = root.Replace('\\', '/').Trim();
        if (!normalized.StartsWith('/'))
        {
            normalized = "/" + normalized;
        }

        return Collapse(normalized).TrimEnd('/');
    }

    public static string Combine(string root, string relativeOrAbsolute)
    {
        var path = ForModel(relativeOrAbsolute);
        if (path.StartsWith('/'))
        {
            return Collapse(path);
        }

        var baseRoot = NormalizeRoot(root);
        if (string.IsNullOrWhiteSpace(path) || path == ".")
        {
            return baseRoot.Length == 0 ? "/" : baseRoot;
        }

        return Collapse(baseRoot.TrimEnd('/') + "/" + path);
    }

    public static string ForModel(string path) =>
        (path ?? string.Empty).Replace('\\', '/').Trim();

    public static bool IsUnderRoot(string fullPath, string root)
    {
        if (string.IsNullOrWhiteSpace(fullPath) || string.IsNullOrWhiteSpace(root))
        {
            return false;
        }

        var normalizedPath = Collapse(ForModel(fullPath));
        var normalizedRoot = NormalizeRoot(root);
        if (normalizedRoot.Length == 0 || normalizedRoot == "/")
        {
            return normalizedPath.StartsWith('/');
        }

        return string.Equals(normalizedPath, normalizedRoot, StringComparison.Ordinal)
               || normalizedPath.StartsWith(normalizedRoot + "/", StringComparison.Ordinal);
    }

    public static string Collapse(string path)
    {
        var parts = ForModel(path).Split('/', StringSplitOptions.RemoveEmptyEntries);
        var stack = new List<string>();
        foreach (var part in parts)
        {
            if (part is "." or "")
            {
                continue;
            }

            if (part == "..")
            {
                if (stack.Count > 0)
                {
                    stack.RemoveAt(stack.Count - 1);
                }

                continue;
            }

            stack.Add(part);
        }

        return "/" + string.Join('/', stack);
    }

    public static string GetFileName(string path)
    {
        var normalized = ForModel(path).TrimEnd('/');
        var index = normalized.LastIndexOf('/');
        return index < 0 ? normalized : normalized[(index + 1)..];
    }

    public static string? GetDirectoryName(string path)
    {
        var normalized = ForModel(path).TrimEnd('/');
        var index = normalized.LastIndexOf('/');
        if (index <= 0)
        {
            return index == 0 ? "/" : null;
        }

        return normalized[..index];
    }
}
