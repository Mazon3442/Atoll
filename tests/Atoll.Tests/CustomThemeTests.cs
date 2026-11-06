using Atoll.Settings;

namespace Atoll.Tests;

public sealed class CustomThemeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "atoll-tests-" + Guid.NewGuid().ToString("N"));

    private SettingsViewModel NewViewModel(out SettingsService service)
    {
        service = new SettingsService(new SettingsStore(Path.Combine(_dir, "settings.json")));
        return new SettingsViewModel(service, []);
    }

    [Fact]
    public void Save_update_apply_and_delete_a_custom_theme()
    {
        var vm = NewViewModel(out var service);
        vm.Background = "#123456";
        vm.Accent = "#FF8800";
        vm.Glass = true;

        vm.NewThemeName = "My Night";
        vm.SaveThemeCommand.Execute(null);

        var saved = Assert.Single(service.Current.CustomThemes);
        Assert.Equal(("My Night", "#123456", "#FF8800", true), (saved.Name, saved.Background, saved.Accent, saved.Glass));
        var card = Assert.Single(vm.Themes, t => t.IsCustom);
        Assert.True(card.IsSelected);

        // Same name (any case) updates instead of duplicating.
        vm.Accent = "#00FF00";
        vm.NewThemeName = "my night";
        vm.SaveThemeCommand.Execute(null);
        Assert.Equal("#00FF00", Assert.Single(service.Current.CustomThemes).Accent);

        // Applying a built-in then the custom one restores its colors.
        vm.ApplyThemeCommand.Execute(vm.Themes.First(t => t.Preset.Name == "Midnight"));
        Assert.False(service.Current.Theme.Glass);
        vm.ApplyThemeCommand.Execute(vm.Themes.Single(t => t.IsCustom));
        Assert.Equal("#00FF00", service.Current.Theme.Accent);
        Assert.True(service.Current.Theme.Glass);

        vm.DeleteThemeCommand.Execute(vm.Themes.Single(t => t.IsCustom));
        Assert.Empty(service.Current.CustomThemes);
        Assert.DoesNotContain(vm.Themes, t => t.IsCustom);
    }

    [Fact]
    public void Built_in_names_and_empty_names_are_refused()
    {
        var vm = NewViewModel(out var service);
        vm.NewThemeName = "midnight";
        vm.SaveThemeCommand.Execute(null);
        vm.NewThemeName = "   ";
        vm.SaveThemeCommand.Execute(null);

        Assert.Empty(service.Current.CustomThemes);
        Assert.NotEqual("", vm.ThemeMessage);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }
}
