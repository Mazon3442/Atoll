namespace Atoll.Modules.Media;

public sealed partial class MediaModule
{
    internal static string FriendlyAppName(string? aumid)
    {
        if (string.IsNullOrEmpty(aumid)) return "";
        var name = aumid.Contains('!') ? aumid[(aumid.LastIndexOf('!') + 1)..] : aumid;
        return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
    }
}
