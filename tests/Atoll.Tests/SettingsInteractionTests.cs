using Atoll.Modules;
using Atoll.Modules.Notifications;
using Atoll.Settings;

namespace Atoll.Tests;

public class SettingsInteractionTests
{
    [Fact]
    public void Choosing_a_city_saves_matching_coordinates_and_legacy_city_edits_clear_them()
    {
        var service = new SettingsService(new SettingsStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json")));
        using var vm = new SettingsViewModel(service, []);
        vm.Weather.Dispose(); // Location persistence is independent of a running forecast request.
        vm.UseWeatherPlaceCommand.Execute(new Atoll.Modules.Weather.Place("Paris, France", 48.85, 2.35));
        Assert.Equal(("Paris, France", 48.85, 2.35), (service.Current.Weather.City, service.Current.Weather.Latitude, service.Current.Weather.Longitude));
        Assert.False(vm.WeatherLocationExpanded);
        vm.WeatherCity = "Chicago";
        Assert.Null(service.Current.Weather.Latitude);
        Assert.Null(service.Current.Weather.Longitude);
    }

    [Fact]
    public void Notification_setup_requires_the_script_package_and_certificate()
    {
        var dir = Path.Combine(Path.GetTempPath(), "atoll-setup-test-" + Guid.NewGuid());
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, "out"));
            File.WriteAllText(Path.Combine(dir, "setup-identity.ps1"), "");
            File.WriteAllText(Path.Combine(dir, "out", "Atoll.Identity.msix"), "");
            Assert.False(NotificationSetup.Complete(dir));
            File.WriteAllText(Path.Combine(dir, "out", "AtollDev.cer"), "");
            Assert.True(NotificationSetup.Complete(dir));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Drag_and_position_menu_share_reordering_and_preserve_unknown_modules()
    {
        var service = new SettingsService(new SettingsStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json")));
        foreach (var descriptor in ModuleCatalog.All) service.Current.Modules.Add(new ModuleEntry { Id = descriptor.Id, Enabled = descriptor.Id != "weather" });
        var unknown = new ModuleEntry { Id = "future-module", Enabled = false };
        service.Current.Modules.Add(unknown);
        using var vm = new SettingsViewModel(service, ModuleCatalog.All);
        var weather = vm.Modules.Single(m => m.Entry.Id == "weather");
        vm.MoveModule(weather, 0);
        Assert.Same(weather, vm.Modules[0]);
        Assert.False(weather.Enabled);
        Assert.Same(unknown, service.Current.Modules.Last());
        Assert.Equal(Enumerable.Range(1, vm.Modules.Count), vm.Modules.Select(m => m.Position));
        weather.Position = 4;
        Assert.Same(weather, vm.Modules[3]);
        Assert.Equal(vm.Modules.Select(m => m.Entry.Id), service.Current.Modules.Take(vm.Modules.Count).Select(m => m.Id));
        weather.Position = 0;
        Assert.Equal(4, weather.Position);
    }

    [Theory]
    [InlineData(NotificationAccess.NeedsSetup, true, "Set up notifications")]
    [InlineData(NotificationAccess.NeedsSetup, false, "Open setup instructions")]
    [InlineData(NotificationAccess.NeedsPermission, true, "Allow access")]
    [InlineData(NotificationAccess.Denied, true, "Open Windows settings")]
    [InlineData(NotificationAccess.Allowed, true, "Preview in the island")]
    [InlineData(NotificationAccess.Unavailable, true, "Open Windows settings")]
    public void Notification_setup_always_explains_the_next_action(NotificationAccess access, bool bundled, string action)
    {
        var guide = NotificationSetupGuide.For(access, bundled);
        Assert.Equal(action, guide.Action);
        Assert.NotEmpty(guide.Description);
    }
}
