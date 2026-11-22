using Atoll.Settings;
using Atoll.Shell;

namespace Atoll.Tests;

public class ProgramRulesTests
{
    [Theory]
    [InlineData("chrome.exe", "chrome")]
    [InlineData("Chrome.EXE", "Chrome")]
    [InlineData(@"C:\Apps\Chrome\chrome.exe", "chrome")]
    [InlineData("\"obs64\"", "obs64")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void Programs_are_named_by_their_file_name_without_exe(string? input, string expected) =>
        Assert.Equal(expected, ProgramRules.Normalize(input), ignoreCase: true);

    [Fact]
    public void A_rule_matches_the_program_in_front_whatever_the_spelling()
    {
        var rules = new[] { new ProgramRule { Process = "Chrome", Mode = FullscreenMode.Hide }, new ProgramRule { Process = "obs64.exe", Mode = FullscreenMode.Dim } };
        Assert.Equal(FullscreenMode.Hide, ProgramRules.Match(rules, "chrome.exe"));
        Assert.Equal(FullscreenMode.Dim, ProgramRules.Match(rules, "OBS64.EXE"));
        Assert.Null(ProgramRules.Match(rules, "notepad.exe"));
        Assert.Null(ProgramRules.Match(rules, null));
        Assert.Null(ProgramRules.Match([new ProgramRule { Process = "" }], "chrome.exe"));
    }

    [Fact]
    public void A_program_rule_beats_the_window_size_setting_and_applies_to_ordinary_windows()
    {
        var settings = new AppSettings { MaximizedMode = FullscreenMode.Stay };
        settings.ProgramRules.Add(new ProgramRule { Process = "chrome", Mode = FullscreenMode.Hide });

        Assert.Equal(FullscreenMode.Hide, IslandWindow.ModeFor(ForegroundKind.Maximized, settings, "chrome.exe"));
        Assert.Equal(FullscreenMode.Hide, IslandWindow.ModeFor(ForegroundKind.Normal, settings, "chrome.exe")); // not only maximized
        Assert.Equal(FullscreenMode.Stay, IslandWindow.ModeFor(ForegroundKind.Maximized, settings, "notepad.exe"));
        Assert.Equal(FullscreenMode.Stay, IslandWindow.ModeFor(ForegroundKind.Maximized, settings)); // no program known
    }

    [Fact]
    public void Rules_survive_a_save()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        try
        {
            var store = new SettingsStore(path);
            var settings = new AppSettings();
            settings.ProgramRules.Add(new ProgramRule { Process = "obs64", Mode = FullscreenMode.PopUp });
            store.Save(settings);
            var rule = Assert.Single(store.Load().ProgramRules);
            Assert.Equal(("obs64", FullscreenMode.PopUp), (rule.Process, rule.Mode));
        }
        finally { File.Delete(path); }
    }
}
