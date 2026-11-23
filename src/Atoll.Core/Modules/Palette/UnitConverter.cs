using System.Globalization;
using System.Text.RegularExpressions;

namespace Atoll.Modules.Palette;

/// <summary>Converts "5 km to mi", "70 f in c", "2 gb to mb" and the like. Pure and offline.</summary>
public static partial class UnitConverter
{
    private enum Kind { Length, Mass, Volume, Speed, Data, Time, Temperature }

    // name -> (kind, how many base units one of it is). Base: metre, gram, litre, m/s, byte, second.
    private static readonly Dictionary<string, (Kind Kind, double Factor, string Label)> Units = Build();

    private static Dictionary<string, (Kind, double, string)> Build()
    {
        var map = new Dictionary<string, (Kind, double, string)>(StringComparer.OrdinalIgnoreCase);
        void Add(Kind kind, double factor, string label, params string[] names)
        {
            foreach (var name in names) map[name] = (kind, factor, label);
        }

        Add(Kind.Length, 1, "m", "m", "meter", "meters", "metre", "metres");
        Add(Kind.Length, 1000, "km", "km", "kilometer", "kilometers", "kilometre", "kilometres");
        Add(Kind.Length, 0.01, "cm", "cm", "centimeter", "centimeters", "centimetre", "centimetres");
        Add(Kind.Length, 0.001, "mm", "mm", "millimeter", "millimeters", "millimetre", "millimetres");
        Add(Kind.Length, 1609.344, "mi", "mi", "mile", "miles");
        Add(Kind.Length, 0.9144, "yd", "yd", "yard", "yards");
        Add(Kind.Length, 0.3048, "ft", "ft", "foot", "feet");
        Add(Kind.Length, 0.0254, "in", "inch", "inches", "\"");
        Add(Kind.Length, 1852, "nmi", "nmi", "nm");

        Add(Kind.Mass, 1, "g", "g", "gram", "grams");
        Add(Kind.Mass, 1000, "kg", "kg", "kilogram", "kilograms", "kilo", "kilos");
        Add(Kind.Mass, 0.001, "mg", "mg", "milligram", "milligrams");
        Add(Kind.Mass, 453.59237, "lb", "lb", "lbs", "pound", "pounds");
        Add(Kind.Mass, 28.349523125, "oz", "oz", "ounce", "ounces");
        Add(Kind.Mass, 1_000_000, "t", "t", "tonne", "tonnes");
        Add(Kind.Mass, 6350.29318, "st", "st", "stone");

        Add(Kind.Volume, 1, "L", "l", "liter", "liters", "litre", "litres");
        Add(Kind.Volume, 0.001, "mL", "ml", "milliliter", "milliliters", "millilitre", "millilitres");
        Add(Kind.Volume, 3.785411784, "gal", "gal", "gallon", "gallons");
        Add(Kind.Volume, 0.946352946, "qt", "qt", "quart", "quarts");
        Add(Kind.Volume, 0.473176473, "pt", "pt", "pint", "pints");
        Add(Kind.Volume, 0.2365882365, "cup", "cup", "cups");
        Add(Kind.Volume, 0.0295735295625, "fl oz", "floz", "fluidounce");
        Add(Kind.Volume, 0.01478676478125, "tbsp", "tbsp", "tablespoon", "tablespoons");
        Add(Kind.Volume, 0.00492892159375, "tsp", "tsp", "teaspoon", "teaspoons");

        Add(Kind.Speed, 1, "m/s", "m/s", "mps");
        Add(Kind.Speed, 1 / 3.6, "km/h", "km/h", "kmh", "kph", "kmph");
        Add(Kind.Speed, 0.44704, "mph", "mph", "mi/h");
        Add(Kind.Speed, 0.514444444, "kn", "kn", "kt", "knot", "knots");

        Add(Kind.Data, 1, "B", "b", "byte", "bytes");
        Add(Kind.Data, 1e3, "KB", "kb");
        Add(Kind.Data, 1e6, "MB", "mb");
        Add(Kind.Data, 1e9, "GB", "gb");
        Add(Kind.Data, 1e12, "TB", "tb");
        Add(Kind.Data, 1024, "KiB", "kib");
        Add(Kind.Data, 1024 * 1024, "MiB", "mib");
        Add(Kind.Data, 1024.0 * 1024 * 1024, "GiB", "gib");
        Add(Kind.Data, 1024.0 * 1024 * 1024 * 1024, "TiB", "tib");

        Add(Kind.Time, 0.001, "ms", "ms", "millisecond", "milliseconds");
        Add(Kind.Time, 1, "s", "s", "sec", "secs", "second", "seconds");
        Add(Kind.Time, 60, "min", "min", "mins", "minute", "minutes");
        Add(Kind.Time, 3600, "h", "h", "hr", "hrs", "hour", "hours");
        Add(Kind.Time, 86400, "d", "d", "day", "days");
        Add(Kind.Time, 604800, "wk", "wk", "week", "weeks");

        Add(Kind.Temperature, 1, "°C", "c", "°c", "celsius", "degc");
        Add(Kind.Temperature, 1, "°F", "f", "°f", "fahrenheit", "degf");
        Add(Kind.Temperature, 1, "K", "k", "kelvin");
        return map;
    }

    /// <summary>"5 km to mi" gives "3.10686 mi". False when it is not a conversion or the units do not match.</summary>
    public static bool TryConvert(string? text, out string result)
    {
        result = "";
        if (text is null) return false;
        var match = Phrase().Match(text.Trim());
        if (!match.Success) return false;
        var amount = double.Parse(match.Groups["n"].Value.Replace(',', '.'), CultureInfo.InvariantCulture);
        if (!Units.TryGetValue(match.Groups["a"].Value, out var from) || !Units.TryGetValue(match.Groups["b"].Value, out var to)) return false;
        if (from.Kind != to.Kind) return false;

        double value;
        if (from.Kind == Kind.Temperature)
            value = FromCelsius(ToCelsius(amount, from.Label), to.Label);
        else value = amount * from.Factor / to.Factor;
        result = Calculator.Format(Math.Round(value, 5)) + " " + to.Label;
        return true;
    }

    private static double ToCelsius(double value, string label) => label switch
    {
        "°F" => (value - 32) * 5 / 9,
        "K" => value - 273.15,
        _ => value,
    };

    private static double FromCelsius(double celsius, string label) => label switch
    {
        "°F" => celsius * 9 / 5 + 32,
        "K" => celsius + 273.15,
        _ => celsius,
    };

    [GeneratedRegex(@"^(?<n>-?\d+(?:[.,]\d+)?)\s*(?<a>[a-zA-Z°/""]+)\s+(?:to|in|into|as|=|->)\s+(?<b>[a-zA-Z°/""]+)$", RegexOptions.IgnoreCase)]
    private static partial Regex Phrase();
}
