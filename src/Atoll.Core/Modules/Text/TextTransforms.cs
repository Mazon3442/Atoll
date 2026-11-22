using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Atoll.Modules.Text;

/// <summary>The output of a transform, or why there is none. Never throws for bad input.</summary>
public readonly record struct TextResult(string Text, string? Error = null)
{
    public bool Ok => Error is null;
    public static TextResult Fail(string error) => new("", error);
}

/// <summary>The Text tab's pure transforms: keyboard layout, case, transliteration, JSON, Base64 and URL encoding.</summary>
public static partial class TextTransforms
{
    // Standard QWERTY and ЙЦУКЕН keys, in the same order, unshifted then shifted.
    private const string Latin = "`qwertyuiop[]asdfghjkl;'zxcvbnm,./~QWERTYUIOP{}ASDFGHJKL:\"ZXCVBNM<>?";
    private const string Cyrillic = "ёйцукенгшщзхъфывапролджэячсмитьбю.ЁЙЦУКЕНГШЩЗХЪФЫВАПРОЛДЖЭЯЧСМИТЬБЮ,";

    /// <summary>
    /// Retypes text typed with the wrong layout: gibberish like "ghbdtn" becomes "привет" and back. The direction
    /// is whichever script the text mostly is not.
    /// </summary>
    public static string FixLayout(string text)
    {
        var cyrillic = text.Count(c => c is >= 'а' and <= 'я' or >= 'А' and <= 'Я' or 'ё' or 'Ё');
        var latin = text.Count(c => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z');
        var (from, to) = cyrillic > latin ? (Cyrillic, Latin) : (Latin, Cyrillic);
        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            var at = from.IndexOf(c);
            sb.Append(at >= 0 ? to[at] : c);
        }
        return sb.ToString();
    }

    public static string Upper(string text) => text.ToUpperInvariant();
    public static string Lower(string text) => text.ToLowerInvariant();

    public static string Title(string text) =>
        CultureInfo.CurrentCulture.TextInfo.ToTitleCase(text.ToLower(CultureInfo.CurrentCulture));

    public static string Sentence(string text) =>
        SentenceStart().Replace(text.ToLower(CultureInfo.CurrentCulture), match => match.Value.ToUpper(CultureInfo.CurrentCulture));

    public static string Snake(string text) => string.Join("_", Words(text).Select(word => word.ToLowerInvariant()));
    public static string Kebab(string text) => string.Join("-", Words(text).Select(word => word.ToLowerInvariant()));

    public static string Camel(string text)
    {
        var words = Words(text).Select(word => word.ToLowerInvariant()).ToList();
        return words.Count == 0 ? "" : words[0] + string.Concat(words.Skip(1).Select(word => char.ToUpperInvariant(word[0]) + word[1..]));
    }

    /// <summary>Splits on spaces, punctuation and camelCase humps, so "fooBar baz-qux" is four words.</summary>
    public static IReadOnlyList<string> Words(string text) =>
        WordPart().Matches(text).Select(match => match.Value).ToList();

    /// <summary>Trims every line and drops the empty ones.</summary>
    public static string CleanLines(string text) =>
        string.Join(Environment.NewLine, text.Split(["\r\n", "\n"], StringSplitOptions.None).Select(line => line.Trim()).Where(line => line.Length > 0));

    // Cyrillic to Latin (the common passport-style scheme); the reverse reads the same table, longest match first.
    private static readonly (string Cyrillic, string Latin)[] Translit =
    [
        ("щ", "shch"), ("ш", "sh"), ("ч", "ch"), ("ж", "zh"), ("ю", "yu"), ("я", "ya"), ("ё", "yo"), ("х", "kh"), ("ц", "ts"),
        ("а", "a"), ("б", "b"), ("в", "v"), ("г", "g"), ("д", "d"), ("е", "e"), ("з", "z"), ("и", "i"), ("й", "y"), ("к", "k"), ("л", "l"),
        ("м", "m"), ("н", "n"), ("о", "o"), ("п", "p"), ("р", "r"), ("с", "s"), ("т", "t"), ("у", "u"), ("ф", "f"), ("ы", "y"), ("ь", ""),
        ("ъ", ""), ("э", "e"),
    ];

    /// <summary>Cyrillic becomes Latin, and Latin becomes Cyrillic, depending on what the text mostly is.</summary>
    public static string Transliterate(string text)
    {
        var cyrillic = text.Count(c => c is >= 'а' and <= 'я' or >= 'А' and <= 'Я' or 'ё' or 'Ё');
        var latin = text.Count(c => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z');
        return cyrillic >= latin && cyrillic > 0 ? ToLatin(text) : ToCyrillic(text);
    }

    private static string ToLatin(string text)
    {
        var sb = new StringBuilder();
        foreach (var c in text)
        {
            var lower = char.ToLowerInvariant(c);
            var hit = Translit.FirstOrDefault(pair => pair.Cyrillic[0] == lower);
            if (hit.Cyrillic is null) { sb.Append(c); continue; }
            var latin = hit.Latin;
            sb.Append(char.IsUpper(c) && latin.Length > 0 ? char.ToUpperInvariant(latin[0]) + latin[1..] : latin);
        }
        return sb.ToString();
    }

    private static string ToCyrillic(string text)
    {
        var ordered = Translit.Where(pair => pair.Latin.Length > 0).OrderByDescending(pair => pair.Latin.Length).ToList();
        var sb = new StringBuilder();
        for (var i = 0; i < text.Length;)
        {
            var match = ordered.FirstOrDefault(pair => string.Compare(text, i, pair.Latin, 0, pair.Latin.Length, StringComparison.OrdinalIgnoreCase) == 0);
            if (match.Cyrillic is null) { sb.Append(text[i++]); continue; }
            var upper = char.IsUpper(text[i]);
            sb.Append(upper ? char.ToUpperInvariant(match.Cyrillic[0]) : match.Cyrillic[0]);
            i += match.Latin.Length;
        }
        return sb.ToString();
    }

    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private static readonly JsonSerializerOptions Compact = new() { WriteIndented = false, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static TextResult JsonPretty(string text) => Json(text, Pretty);
    public static TextResult JsonMinify(string text) => Json(text, Compact);

    private static TextResult Json(string text, JsonSerializerOptions options)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            return new TextResult(JsonSerializer.Serialize(document.RootElement, options));
        }
        catch (JsonException ex)
        {
            return TextResult.Fail($"Not valid JSON: {ex.Message}");
        }
    }

    public static string Base64Encode(string text) => Convert.ToBase64String(Encoding.UTF8.GetBytes(text));

    public static TextResult Base64Decode(string text)
    {
        try
        {
            var cleaned = Regex.Replace(text.Trim(), @"\s+", "").Replace('-', '+').Replace('_', '/');
            cleaned = cleaned.PadRight(cleaned.Length + (4 - cleaned.Length % 4) % 4, '=');
            return new TextResult(new UTF8Encoding(false, true).GetString(Convert.FromBase64String(cleaned)));
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            return TextResult.Fail("That is not valid Base64 text.");
        }
    }

    public static string UrlEncode(string text) => Uri.EscapeDataString(text);

    public static TextResult UrlDecode(string text)
    {
        try { return new TextResult(Uri.UnescapeDataString(text.Replace('+', ' '))); }
        catch (UriFormatException) { return TextResult.Fail("That is not valid URL text."); }
    }

    [GeneratedRegex(@"(?:^|(?<=[.!?]\s))\p{L}", RegexOptions.Multiline)]
    private static partial Regex SentenceStart();

    [GeneratedRegex(@"\p{Lu}+(?!\p{Ll})|\p{Lu}?\p{Ll}+|\d+")]
    private static partial Regex WordPart();
}
