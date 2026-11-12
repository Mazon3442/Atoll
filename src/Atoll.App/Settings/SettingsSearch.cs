namespace Atoll.Settings;

/// <summary>Matching for the settings search box.</summary>
internal static class SettingsSearch
{
    /// <summary>
    /// 2 when every word of <paramref name="query"/> is in the setting's title, 1 when every word is somewhere in the
    /// title or <paramref name="rest"/> (description, section, page), 0 otherwise. Case does not matter.
    /// </summary>
    internal static int Score(string query, string header, string rest)
    {
        var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length == 0) return 0;
        if (words.All(word => header.Contains(word, StringComparison.OrdinalIgnoreCase))) return 2;
        var all = header + " " + rest;
        return words.All(word => all.Contains(word, StringComparison.OrdinalIgnoreCase)) ? 1 : 0;
    }
}
