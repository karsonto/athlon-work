using Athlon.Agent.Core;

namespace Athlon.Agent.Infrastructure.Patch;

internal static class PatchApplyFilter
{
    public static bool Matches(PatchFile file, string? pathFilter)
    {
        if (pathFilter is null)
        {
            return true;
        }

        var filter = ToolPathNormalizer.ForModel(pathFilter);
        if (string.Equals(ToolPathNormalizer.ForModel(file.Path), filter, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return file.MoveTo is not null
            && string.Equals(ToolPathNormalizer.ForModel(file.MoveTo), filter, StringComparison.OrdinalIgnoreCase);
    }
}
