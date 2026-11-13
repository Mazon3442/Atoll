using Atoll.Settings;

namespace Atoll.Tests;

public sealed class SettingsStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "atoll-tests-" + Guid.NewGuid().ToString("N"));
    private string FilePath => Path.Combine(_dir, "settings.json");

    [Fact]
    public void Missing_file_gives_defaults()
    {
        var s = new SettingsStore(FilePath).Load();
        Assert.Equal(FullscreenMode.PopUp, s.FullscreenMode);
        Assert.True(s.ClickThrough);
        Assert.Equal(MonitorMode.Primary, s.MonitorMode);
        Assert.Equal(IslandShape.Pill, s.Theme.Shape);
    }

    [Fact]
    public void Save_then_load_round_trips()
    {
        var store = new SettingsStore(FilePath);
        var s = new AppSettings { MonitorMode = MonitorMode.All, TopOffset = 12 };
        s.Theme.Accent = "#FF00FF";
        s.Modules.Add(new ModuleEntry { Id = "clock", Enabled = false });
        s.Games.ShowPerformance = false;
        s.Games.ScanEpic = false;
        s.Games.ServerRefreshSeconds = 120;
        s.Pomodoro.FocusMinutes = 50;
        s.Pomodoro.ShortBreakMinutes = 3;
        s.Pomodoro.LongBreakMinutes = 20;
        s.Pomodoro.SessionsBeforeLongBreak = 2;
        store.Save(s);

        var loaded = store.Load();

        Assert.Equal(MonitorMode.All, loaded.MonitorMode);
        Assert.Equal(12, loaded.TopOffset);
        Assert.Equal("#FF00FF", loaded.Theme.Accent);
        Assert.False(Assert.Single(loaded.Modules).Enabled);
        Assert.False(loaded.Games.ShowPerformance);
        Assert.False(loaded.Games.ScanEpic);
        Assert.Equal(120, loaded.Games.ServerRefreshSeconds);
        Assert.Equal(50, loaded.Pomodoro.FocusMinutes);
        Assert.Equal(3, loaded.Pomodoro.ShortBreakMinutes);
        Assert.Equal(20, loaded.Pomodoro.LongBreakMinutes);
        Assert.Equal(2, loaded.Pomodoro.SessionsBeforeLongBreak);
        Assert.Contains("\"All\"", File.ReadAllText(FilePath)); // enums stored as names
    }

    [Fact]
    public void Corrupt_file_is_backed_up_and_defaults_used()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(FilePath, "{ not json");

        var s = new SettingsStore(FilePath).Load();

        Assert.Equal(MonitorMode.Primary, s.MonitorMode);
        Assert.Equal("{ not json", File.ReadAllText(FilePath + ".bad"));
    }

    [Theory]
    [InlineData(IslandShape.Pill)]
    [InlineData(IslandShape.Notch)]
    public void Island_shape_round_trips(IslandShape shape)
    {
        var store = new SettingsStore(FilePath);
        var settings = new AppSettings();
        settings.Theme.Shape = shape;

        store.Save(settings);

        Assert.Equal(shape, store.Load().Theme.Shape);
        Assert.Contains($"\"{shape}\"", File.ReadAllText(FilePath));
    }

    [Fact]
    public void Settings_without_a_shape_use_pill()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(FilePath, "{\"Theme\":{}}");

        Assert.Equal(IslandShape.Pill, new SettingsStore(FilePath).Load().Theme.Shape);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }
}
