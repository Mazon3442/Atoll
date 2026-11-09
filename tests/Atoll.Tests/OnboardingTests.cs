using Atoll.Modules;
using Atoll.Onboarding;
using Atoll.Settings;

namespace Atoll.Tests;

public sealed class OnboardingTests
{
    [Fact]
    public void Welcome_starts_with_the_requested_top_row()
    {
        var settings = new AppSettings();
        var vm = new OnboardingViewModel(settings, () => { });

        Assert.Equal(["home", "claude", "media", "notifications", "calendar", "shelf", "clipboard", "weather"],
            vm.Tabs.Where(tab => tab.Bookmarked).Select(tab => tab.Id));
        Assert.Contains(vm.Tabs, tab => tab.Id == "clock" && !tab.Bookmarked);
        Assert.DoesNotContain(vm.Tabs, tab => tab.Id == "providers"); // off until an API key exists
        Assert.Equal("Takes about a minute", vm.StepLabel);

        vm.NextCommand.Execute(null);
        Assert.True(vm.IsPicker);
        vm.NextCommand.Execute(null);
        Assert.True(vm.IsReady);
        vm.BackCommand.Execute(null);
        Assert.True(vm.IsPicker);
    }

    [Fact]
    public void Finishing_writes_bookmarks_and_keeps_unstarred_tabs_enabled()
    {
        var settings = new AppSettings();
        var saves = 0;
        var startup = new List<bool>();
        var vm = new OnboardingViewModel(settings, () => saves++, startup.Add);

        vm.Tabs.Single(tab => tab.Id == "clock").Bookmarked = true;
        vm.Tabs.Single(tab => tab.Id == "media").Bookmarked = false;
        vm.StartWithWindows = true;
        vm.FinishCommand.Execute(null);

        Assert.True(settings.OnboardingComplete);
        Assert.True(settings.StartWithWindows);
        Assert.Equal([true], startup);
        Assert.True(settings.Modules.Single(m => m.Id == "clock").Bookmarked);
        Assert.True(settings.Modules.Single(m => m.Id == "clock").Enabled);
        Assert.False(settings.Modules.Single(m => m.Id == "media").Bookmarked);
        Assert.True(settings.Modules.Single(m => m.Id == "media").Enabled);
        Assert.Equal(1, saves);
    }

    [Fact]
    public void Closing_the_welcome_skips_it_without_changing_the_top_row()
    {
        var settings = new AppSettings();
        var saves = 0;
        var vm = new OnboardingViewModel(settings, () => saves++);
        vm.Tabs.Single(tab => tab.Id == "clock").Bookmarked = true;

        vm.Dismiss();

        Assert.True(settings.OnboardingComplete);
        Assert.Empty(settings.Modules); // the picker was not applied
        Assert.Equal(1, saves);
        vm.Dismiss();
        Assert.Equal(1, saves);
    }

    [Fact]
    public void Catalog_defaults_bookmark_the_initial_top_row_only()
    {
        string[] topRow = ["claude", "media", "notifications", "calendar", "shelf", "clipboard", "weather"];
        Assert.All(ModuleCatalog.All, descriptor =>
            Assert.Equal(topRow.Contains(descriptor.Id), descriptor.BookmarkedByDefault));
    }
}
