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
    public void Adding_an_api_key_validates_and_encrypts_it_and_switches_the_feature_on()
    {
        var service = new SettingsService(new SettingsStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json")));
        using var vm = new SettingsViewModel(service, ModuleCatalog.All);
        vm.Weather.Dispose();
        Assert.False(service.Current.Modules.Single(m => m.Id == "providers").Enabled); // no empty tab for new users

        vm.ProviderPreset = "";
        vm.NewProviderName = "Local model";
        vm.NewProviderUrl = "http://localhost:8080/v1/";
        Assert.False(vm.AddProvider("sk-test", ""));
        Assert.Contains("HTTPS", vm.ProviderMessage);
        vm.ProviderPreset = "https://api.anthropic.com/v1/";
        Assert.Equal(("Anthropic", "https://api.anthropic.com/v1/"), (vm.NewProviderName, vm.NewProviderUrl));
        Assert.False(vm.AddProvider("", ""));
        Assert.Empty(service.Current.Providers.Accounts);

        Assert.True(vm.AddProvider("  sk-ant-test  ", ""));
        var account = Assert.Single(service.Current.Providers.Accounts);
        Assert.DoesNotContain("sk-ant-test", account.EncryptedKey);
        Assert.Equal("sk-ant-test", SecretStore.Unprotect(account.EncryptedKey));
        Assert.True(vm.GatewayEnabled);
        Assert.True(vm.GatewayToken().Length >= 32);
        Assert.True(service.Current.Modules.Single(m => m.Id == "providers").Enabled);
        Assert.Equal($"http://127.0.0.1:47831/providers/{account.Id}/v1", Assert.Single(vm.ProviderAccounts).GatewayUrl);

        vm.RemoveProviderCommand.Execute(vm.ProviderAccounts[0]);
        Assert.Empty(service.Current.Providers.Accounts);
        Assert.Empty(vm.ProviderAccounts);
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
        // API keys is new to these saved settings, so it is added switched off at the end.
        Assert.Equal(["System", "Agents", "Media", "Clipboard", "Notifications", "Timer", "Calendar", "Files", "Notes", "Launcher", "Weather", "API keys"], vm.ModuleTabs.Select(t => t.Name));
        Assert.False(vm.ModuleTabs.Single(t => t.Id == "providers").IsShown);
        Assert.Equal(["System", "Agents", "Media", "Clipboard", "Notifications", "Timer", "Calendar", "Files", "Notes", "Launcher"], vm.ModuleTabs.Where(t => t.IsShown).Select(t => t.Name));
        Assert.Equal(["Status popups", "Live graphs", "Quick actions", "Camera and mic in use"], vm.ModuleTabs[0].Features.Select(f => f.Name));
        Assert.True(vm.ModuleTabs[0].IsFirst);

        // Each tab's features are kept together, so the island's order is exactly the card order.
        Assert.Equal(["system", "stats", "actions", "privacy", "claude", "media", "clipboard", "notifications", "clock", "calendar", "shelf", "notes", "launcher", "games", "weather", "providers"],
            service.Current.Modules.Take(16).Select(m => m.Id));
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
        // System's features are not bookmarked by default, so the tab waits in the ⋯ menu.
        Assert.Equal("In the ⋯ menu, not the top row", system.Status);
        system.Features[0].Bookmarked = true;
        Assert.Equal("1 of 3 in the top row", system.Status);
        vm.MoveTab(system, 99);
        Assert.Equal(3, system.Number);
        Assert.Same(unknown, service.Current.Modules.Last());
    }

    [Fact]
    public void Features_can_be_bookmarked_in_the_top_row_or_switched_off_entirely()
    {
        var service = new SettingsService(new SettingsStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json")));
        using var vm = new SettingsViewModel(service, ModuleCatalog.All);
        var weather = vm.ModuleTabs.Single(t => t.Id == "weather");
        var entry = service.Current.Modules.Single(m => m.Id == "weather");

        // Existing and new features are bookmarked until moved.
        Assert.True(weather.OnlyFeature.Bookmarked);
        Assert.Equal(FeaturePlacement.Bookmarked, entry.Placement);

        // Unstarring keeps it on, in the ⋯ menu instead of the top row.
        weather.OnlyFeature.Bookmarked = false;
        Assert.True(weather.OnlyFeature.Enabled);
        Assert.Equal(FeaturePlacement.Menu, entry.Placement);
        Assert.Contains("menu", weather.Status);

        // Off hides it from both places, and turning it back on remembers the menu.
        weather.OnlyFeature.Enabled = false;
        Assert.Equal(FeaturePlacement.Off, entry.Placement);
        Assert.StartsWith("Not in the island", weather.Status);
        weather.OnlyFeature.Enabled = true;
        Assert.Equal(FeaturePlacement.Menu, entry.Placement);

        // Starring it again puts it in the top row.
        weather.OnlyFeature.Bookmarked = true;
        Assert.Equal(FeaturePlacement.Bookmarked, entry.Placement);

        // Home follows the same three states.
        vm.HomeBookmarked = false;
        Assert.True(vm.HomeEnabled);
        Assert.Equal(FeaturePlacement.Menu, service.Current.Home.Placement);
        vm.HomeEnabled = false;
        Assert.Equal(FeaturePlacement.Off, service.Current.Home.Placement);
        vm.HomeBookmarked = true;
        Assert.True(vm.HomeEnabled);
        Assert.Equal(FeaturePlacement.Bookmarked, service.Current.Home.Placement);
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
