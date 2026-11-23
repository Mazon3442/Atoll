using Atoll.Modules.Palette;

namespace Atoll.Tests;

public class PaletteProviderTests
{
    [Theory]
    [InlineData("2+2", 4)]
    [InlineData("2 + 3 * 4", 14)]
    [InlineData("(2 + 3) * 4", 20)]
    [InlineData("2^3^2", 512)]
    [InlineData("-5 + 10", 5)]
    [InlineData("10 / 4", 2.5)]
    [InlineData("1,5 * 2", 3)]
    [InlineData("sqrt(81) + 1", 10)]
    [InlineData("15% of 80", 12)]
    [InlineData("= 6 × 7", 42)]
    [InlineData("2 * pi", Math.Tau)]
    [InlineData("sin(90)", 1)]
    public void Expressions_are_evaluated(string text, double expected)
    {
        Assert.True(Calculator.TryEvaluate(text, out var result), text);
        Assert.Equal(expected, result, 9);
    }

    [Theory]
    [InlineData("")]
    [InlineData("42")]
    [InlineData("hello")]
    [InlineData("2 +")]
    [InlineData("(2 + 3")]
    [InlineData("1 / 0")]
    [InlineData("system(\"calc\")")]
    [InlineData("2 ** 3")]
    public void Anything_else_is_left_to_the_other_results(string text) =>
        Assert.False(Calculator.TryEvaluate(text, out _));

    [Theory]
    [InlineData(1234.5, "1,234.5")]
    [InlineData(0.1 + 0.2, "0.3")]
    [InlineData(4, "4")]
    [InlineData(1e20, "1E+20")]
    public void Results_are_formatted_without_noise(double value, string expected) =>
        Assert.Equal(expected, Calculator.Format(value).Replace("1.E", "1E"));

    [Theory]
    [InlineData("5 km to mi", "3.10686 mi")]
    [InlineData("100 c in f", "212 °F")]
    [InlineData("32 f to c", "0 °C")]
    [InlineData("0 c to k", "273.15 K")]
    [InlineData("2 gb to mb", "2,000 MB")]
    [InlineData("1 gib to mib", "1,024 MiB")]
    [InlineData("3 lb to kg", "1.36078 kg")]
    [InlineData("60 mph to km/h", "96.56064 km/h")]
    [InlineData("90 min to h", "1.5 h")]
    [InlineData("1 gallon to l", "3.78541 L")]
    public void Units_convert(string text, string expected)
    {
        Assert.True(UnitConverter.TryConvert(text, out var result), text);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("5 km to kg")]   // different kinds
    [InlineData("5 foo to bar")]
    [InlineData("kilometers")]
    [InlineData("5 km")]
    public void Units_that_do_not_match_are_refused(string text) =>
        Assert.False(UnitConverter.TryConvert(text, out _));

    [Fact]
    public void Currencies_convert_through_the_base_rate()
    {
        Assert.True(CurrencyConverter.TryParse("100 usd to eur", out var amount, out var from, out var to));
        Assert.Equal((100, "USD", "EUR"), (amount, from, to));
        var rates = new Dictionary<string, double> { ["USD"] = 1, ["EUR"] = 0.9, ["PLN"] = 4 };
        Assert.True(CurrencyConverter.TryConvert(amount, from, to, rates, out var result));
        Assert.Equal(90, result, 9);
        Assert.True(CurrencyConverter.TryConvert(90, "EUR", "PLN", rates, out result));
        Assert.Equal(400, result, 9);
        Assert.False(CurrencyConverter.TryConvert(1, "USD", "XXX", rates, out _));
        Assert.False(CurrencyConverter.TryParse("100 usd to usd", out _, out _, out _));
        Assert.False(CurrencyConverter.TryParse("100 km to mi", out _, out _, out _)); // units are not currencies
    }

    [Theory]
    [InlineData("timer 10 min", 600)]
    [InlineData("timer 10", 600)]
    [InlineData("timer 1h 30m", 5400)]
    [InlineData("timer 90s", 90)]
    [InlineData("5 minute timer", 300)]
    [InlineData("countdown 2 hours", 7200)]
    public void Timer_phrases_name_a_length(string text, double seconds)
    {
        Assert.True(TimerPhrase.TryParse(text, out var length), text);
        Assert.Equal(seconds, length.TotalSeconds);
    }

    [Theory]
    [InlineData("timer")]
    [InlineData("timer 0")]
    [InlineData("timer 48 hours")]
    [InlineData("kitchen")]
    public void Other_text_is_not_a_timer(string text) => Assert.False(TimerPhrase.TryParse(text, out _));

    [Fact]
    public void Stopwatch_is_recognised() => Assert.True(TimerPhrase.IsStopwatch(" Stopwatch "));

    [Theory]
    [InlineData("> ipconfig /all", ShellPhrase.Shell.PowerShell, "ipconfig /all")]
    [InlineData(">cmd dir /b", ShellPhrase.Shell.Cmd, "dir /b")]
    public void Shell_lines_pick_a_shell(string text, ShellPhrase.Shell shell, string command)
    {
        Assert.True(ShellPhrase.TryParse(text, out var parsed, out var line));
        Assert.Equal((shell, command), (parsed, line));
    }

    [Theory]
    [InlineData("ipconfig")]
    [InlineData(">")]
    [InlineData(">   ")]
    public void Without_a_command_there_is_nothing_to_run(string text) => Assert.False(ShellPhrase.TryParse(text, out _, out _));
}

public class PaletteQueryTests
{
    [Fact]
    public void File_names_rank_exact_then_prefix_then_contains_and_need_every_word()
    {
        var index = new[]
        {
            new Atoll.Modules.Palette.IndexedPath("old report 2020.docx", @"C:\Docs\old report 2020.docx", false),
            new Atoll.Modules.Palette.IndexedPath("report.pdf", @"C:\Docs\Work\report.pdf", false),
            new Atoll.Modules.Palette.IndexedPath("report", @"C:\Docs\report", true),
            new Atoll.Modules.Palette.IndexedPath("reports archive", @"C:\Docs\reports archive", true),
            new Atoll.Modules.Palette.IndexedPath("notes.txt", @"C:\Docs\notes.txt", false),
        };

        var ranked = Atoll.Modules.Palette.FileNameSearch.Rank(index, "report", 10).Select(item => item.Name);
        Assert.Equal(["report", "report.pdf", "reports archive", "old report 2020.docx"], ranked);

        Assert.Equal(["old report 2020.docx"], Atoll.Modules.Palette.FileNameSearch.Rank(index, "2020 report", 10).Select(item => item.Name));
        Assert.Empty(Atoll.Modules.Palette.FileNameSearch.Rank(index, "   ", 10));
        Assert.Single(Atoll.Modules.Palette.FileNameSearch.Rank(index, "report", 1));
    }

    private static Atoll.Shell.CommandPaletteViewModel Create(List<string> ran)
    {
        var items = new List<Atoll.Shell.CommandItem>
        {
            new("Screenshot", "Action", "", "", Atoll.Shell.CommandKind.Action, () => ran.Add("screenshot")),
        };
        return new Atoll.Shell.CommandPaletteViewModel(() => items, text => new Atoll.Shell.QueryItems(
            [new("= 4", "Calculator", "", "", Atoll.Shell.CommandKind.Result, () => ran.Add("copy"))],
            [new($"Search the web for {text}", "Browser", "", "", Atoll.Shell.CommandKind.Fallback, () => ran.Add("web"))]));
    }

    [Fact]
    public void Answers_come_first_and_the_fallbacks_stay_last()
    {
        var vm = Create([]);
        vm.Refresh();
        vm.Query = "scr";

        Assert.Equal(["= 4", "Screenshot", "Search the web for scr"], vm.Results.Select(item => item.Title));
        Assert.Equal("= 4", vm.Selected?.Title);
    }

    [Fact]
    public void An_empty_query_adds_no_answers_or_fallbacks()
    {
        var vm = Create([]);
        vm.Refresh();
        Assert.Equal(["Screenshot"], vm.Results.Select(item => item.Title));
    }

    [Fact]
    public void Ctrl_Enter_runs_the_folder_action_and_falls_back_to_the_normal_one()
    {
        var ran = new List<string>();
        var item = new Atoll.Shell.CommandItem("report.pdf", @"C:\Docs", "", "", Atoll.Shell.CommandKind.File,
            () => ran.Add("open"), RunFolder: () => ran.Add("folder"), RunElevated: () => ran.Add("admin"));
        var plain = new Atoll.Shell.CommandItem("Lock", "Action", "", "", Atoll.Shell.CommandKind.Action, () => ran.Add("lock"));
        var vm = new Atoll.Shell.CommandPaletteViewModel(() => [item, plain]);
        vm.Refresh();

        vm.Selected = item;
        vm.RunSelected();
        vm.Selected = item;
        vm.RunSelected(folder: true);
        vm.Selected = item;
        vm.RunSelected(elevated: true);
        vm.Selected = plain;
        vm.RunSelected(folder: true); // nothing special: it just runs

        Assert.Equal(["open", "folder", "admin", "lock"], ran);
    }
}
