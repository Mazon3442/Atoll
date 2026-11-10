using Atoll.Modules.Tide;
using Atoll.Settings;

namespace Atoll.Tests;

public sealed class TideTests
{
    [Fact]
    public void Edited_script_is_written_back_to_settings()
    {
        var settings = new TideSettings();
        var saved = 0;
        using var vm = new TideViewModel(settings, () => saved++);

        vm.Title = "Launch notes";
        vm.Body = "Good morning, everyone.";
        vm.SaveNow();

        var script = Assert.Single(settings.Scripts);
        Assert.Equal("Launch notes", script.Title);
        Assert.Equal("Good morning, everyone.", script.Body);
        Assert.Equal(script.Id, settings.SelectedScriptId);
        Assert.True(saved > 0);
    }

    [Fact]
    public void New_and_deleted_scripts_stay_selected_and_saved()
    {
        var settings = new TideSettings();
        using var vm = new TideViewModel(settings, () => { });
        vm.NewScriptCommand.Execute(null);
        vm.Title = "Second";
        vm.SaveNow();

        Assert.Equal(2, settings.Scripts.Count);
        Assert.Equal("Second", vm.SelectedScript?.Title);

        vm.DeleteScriptCommand.Execute(null);
        Assert.Single(vm.Scripts);
        Assert.NotNull(vm.SelectedScript);
    }

    [Fact]
    public void Speed_presets_change_the_label()
    {
        var settings = new TideSettings();
        using var vm = new TideViewModel(settings, () => { });
        vm.SlowCommand.Execute(null);
        Assert.Equal("Slow", vm.SpeedLabel);
        vm.SprintCommand.Execute(null);
        Assert.Equal("Sprint", vm.SpeedLabel);
    }
}
