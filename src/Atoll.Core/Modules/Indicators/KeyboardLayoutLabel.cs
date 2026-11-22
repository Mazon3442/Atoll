using System.Globalization;

namespace Atoll.Modules.Indicators;

/// <summary>Names a keyboard layout the way the layout chip shows it: "EN" and "English".</summary>
public static class KeyboardLayoutLabel
{
    /// <summary>The label for a Windows language id (the low word of a keyboard layout handle).</summary>
    public static (string Code, string Name) For(int languageId)
    {
        try
        {
            var culture = CultureInfo.GetCultureInfo(languageId & 0xFFFF);
            var name = culture.EnglishName;
            var bracket = name.IndexOf(" (", StringComparison.Ordinal);
            return (culture.TwoLetterISOLanguageName.ToUpperInvariant(), bracket > 0 ? name[..bracket] : name);
        }
        catch (CultureNotFoundException)
        {
            return ("??", "Unknown layout");
        }
    }
}