namespace Atoll.Modules.Palette;

/// <summary>One file or folder in the in-memory name index.</summary>
public sealed record IndexedPath(string Name, string Path, bool IsDirectory);

/// <summary>Ranks indexed names for a palette query. Pure, so the order is testable.</summary>
public static class FileNameSearch
{
    /// <summary>
    /// Exact name first, then names that start with the query, then names that contain it; folders before files
    /// within a tier, then the shorter path (closer to the top of the drive). Every word of the query must match.
    /// </summary>
    public static IReadOnlyList<IndexedPath> Rank(IEnumerable<IndexedPath> index, string query, int max)
    {
        var words = query.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0 || max <= 0) return [];
        var whole = query.Trim();

        int Tier(IndexedPath item)
        {
            var stem = System.IO.Path.GetFileNameWithoutExtension(item.Name);
            if (item.Name.Equals(whole, StringComparison.OrdinalIgnoreCase) || stem.Equals(whole, StringComparison.OrdinalIgnoreCase)) return 0;
            if (item.Name.StartsWith(whole, StringComparison.OrdinalIgnoreCase)) return 1;
            return item.Name.Contains(whole, StringComparison.OrdinalIgnoreCase) ? 2 : 3;
        }

        return index
            .Where(item => words.All(word => item.Name.Contains(word, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(Tier)
            .ThenBy(item => item.IsDirectory ? 0 : 1)
            .ThenBy(item => item.Path.Length)
            .ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
            .Take(max)
            .ToList();
    }
}
