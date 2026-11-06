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
    public void Modules_page_orders_whole_tabs_the_way_the_island_shows_them()
    {
        var service = new SettingsService(new SettingsStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json")));
        // Scattered order: Quick actions (System tab) comes first, and Weather is off.
        foreach (var id in new[] { "actions", "claude", "media", "weather", "clipboard", "notifications", "clock", "calendar", "system", "shelf", "stats" })
            service.Current.Modules.Add(new ModuleEntry { Id = id, Enabled = id != "weather" });
        var unknown = new ModuleEntry { Id = "future-module", Enabled = false };
        service.Current.Modules.Add(unknown);
        using var vm = new SettingsViewModel(service, ModuleCatalog.All);

        // One card per tab, placed where the island puts it: at its first switched-on feature.
        Assert.Equal(["System", "Agents", "Media", "Files", "Notifications", "Clock & calendar", "Weather"], vm.ModuleTabs.Select(t => t.Name));
        Assert.Equal(["System", "Agents", "Media", "Files", "Notifications", "Clock & calendar"], vm.ModuleTabs.Where(t => t.IsShown).Select(t => t.Name));
        Assert.Equal(["Status popups", "Live graphs", "Quick actions"], vm.ModuleTabs[0].Features.Select(f => f.Name));
        Assert.True(vm.ModuleTabs[0].IsFirst);

        // Each tab's features are kept together, so the island's order is exactly the card order.
        Assert.Equal(["system", "stats", "actions", "claude", "media", "shelf", "clipboard", "notifications", "clock", "calendar", "weather"],
            service.Current.Modules.Take(11).Select(m => m.Id));
        Assert.Same(unknown, service.Current.Modules.Last());

        var weather = vm.ModuleTabs.Single(t => t.Id == "weather");
        Assert.False(weather.IsShown);
        Assert.StartsWith("Not in the island", weather.Status);
        vm.MoveTab(weather, 0);
        Assert.Same(weather, vm.ModuleTabs[0]);
        Assert.Equal(["weather", "system", "stats", "actions"], service.Current.Modules.Take(4).Select(m => m.Id));
        weather.OnlyFeature.Enabled = true;
        Assert.Equal("Weather", vm.ModuleTabs.First(t => t.IsShown).Name);

        var system = vm.ModuleTabs.Single(t => t.Id == "system");
        vm.MoveTabDownCommand.Execute(system);
        Assert.Equal(3, system.Number);
        system.Features.Single(f => f.Entry.Id == "stats").Enabled = false;
        Assert.Equal("2 of 3 on", system.Status);
        vm.MoveTab(system, 99);
        Assert.Equal(3, system.Number);
        Assert.Same(unknown, service.Current.Modules.Last());
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
