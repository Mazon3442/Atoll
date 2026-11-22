using System.Globalization;
using System.Text.RegularExpressions;

namespace Atoll.Modules.Palette;

/// <summary>
/// A small, safe calculator for the command palette: + - * / ^, brackets, percent ("15% of 80"), pi, e and a few
/// functions. It is a hand-written parser, so nothing a person types is ever executed as code.
/// </summary>
public static partial class Calculator
{
    /// <summary>
    /// True when <paramref name="text"/> is an expression that does arithmetic. A bare number is not, so typing
    /// "42" in the palette does not offer "= 42".
    /// </summary>
    public static bool TryEvaluate(string? text, out double result)
    {
        result = 0;
        if (string.IsNullOrWhiteSpace(text) || text.Length > 200) return false;
        var source = text.Trim().TrimStart('=').Trim();
        var percent = PercentOf().Match(source);
        if (percent.Success)
        {
            result = Number(percent.Groups["p"].Value) / 100 * Number(percent.Groups["n"].Value);
            return double.IsFinite(result);
        }
        source = source.Replace('×', '*').Replace('÷', '/').Replace('−', '-').Replace(',', '.');
        if (!source.Any(char.IsDigit) && !source.Contains("pi", StringComparison.OrdinalIgnoreCase)) return false;
        var parser = new Parser(source);
        if (!parser.TryParse(out result) || !double.IsFinite(result)) return false;
        return parser.DidArithmetic;
    }

    /// <summary>A result as people write it: no trailing zeros, at most 10 significant digits.</summary>
    public static string Format(double value)
    {
        var rounded = Math.Round(value, 10);
        if (Math.Abs(rounded) >= 1e15 || (rounded != 0 && Math.Abs(rounded) < 1e-9))
            return rounded.ToString("0.#########E+0", CultureInfo.InvariantCulture);
        return rounded.ToString("#,0.##########", CultureInfo.InvariantCulture);
    }

    private static double Number(string text) => double.Parse(text.Replace(',', '.'), CultureInfo.InvariantCulture);

    [GeneratedRegex(@"^(?<p>\d+(?:[.,]\d+)?)\s*%\s*of\s*(?<n>\d+(?:[.,]\d+)?)$", RegexOptions.IgnoreCase)]
    private static partial Regex PercentOf();

    private sealed class Parser(string text)
    {
        private int _at;
        public bool DidArithmetic { get; private set; }

        public bool TryParse(out double value)
        {
            value = 0;
            try
            {
                value = Expression();
                Skip();
                return _at == text.Length;
            }
            catch (FormatException) { return false; }
        }

        private double Expression()
        {
            var left = Term();
            while (true)
            {
                Skip();
                if (Take('+')) { left += Term(); DidArithmetic = true; }
                else if (Take('-')) { left -= Term(); DidArithmetic = true; }
                else return left;
            }
        }

        private double Term()
        {
            var left = Power();
            while (true)
            {
                Skip();
                if (Take('*')) { left *= Power(); DidArithmetic = true; }
                else if (Take('/')) { left /= Power(); DidArithmetic = true; }
                else return left;
            }
        }

        private double Power()
        {
            var left = Unary();
            Skip();
            if (!Take('^')) return left;
            DidArithmetic = true;
            return Math.Pow(left, Power()); // right associative
        }

        private double Unary()
        {
            Skip();
            if (Take('-')) return -Unary();
            if (Take('+')) return Unary();
            return Primary();
        }

        private double Primary()
        {
            Skip();
            if (Take('('))
            {
                var inner = Expression();
                Skip();
                if (!Take(')')) throw new FormatException();
                return inner;
            }
            if (_at < text.Length && (char.IsDigit(text[_at]) || text[_at] == '.')) return Literal();
            var name = Name();
            if (name.Length == 0) throw new FormatException();
            switch (name)
            {
                case "pi": return Math.PI;
                case "e": return Math.E;
            }
            Skip();
            if (!Take('(')) throw new FormatException();
            var argument = Expression();
            Skip();
            if (!Take(')')) throw new FormatException();
            DidArithmetic = true;
            return name switch
            {
                "sqrt" => Math.Sqrt(argument),
                "abs" => Math.Abs(argument),
                "round" => Math.Round(argument),
                "floor" => Math.Floor(argument),
                "ceil" => Math.Ceiling(argument),
                "sin" => Math.Sin(argument * Math.PI / 180),
                "cos" => Math.Cos(argument * Math.PI / 180),
                "tan" => Math.Tan(argument * Math.PI / 180),
                "ln" => Math.Log(argument),
                "log" => Math.Log10(argument),
                _ => throw new FormatException(),
            };
        }

        private double Literal()
        {
            var start = _at;
            while (_at < text.Length && (char.IsDigit(text[_at]) || text[_at] == '.')) _at++;
            return double.TryParse(text.AsSpan(start, _at - start), CultureInfo.InvariantCulture, out var value) ? value : throw new FormatException();
        }

        private string Name()
        {
            var start = _at;
            while (_at < text.Length && char.IsLetter(text[_at])) _at++;
            return text[start.._at].ToLowerInvariant();
        }

        private void Skip()
        {
            while (_at < text.Length && char.IsWhiteSpace(text[_at])) _at++;
        }

        private bool Take(char c)
        {
            if (_at >= text.Length || text[_at] != c) return false;
            _at++;
            return true;
        }
    }
}
