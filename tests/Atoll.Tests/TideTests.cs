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
    public void Hide_chip_names_the_action_it_will_take()
    {
        // The prompter hides from capture by default, so the chip offers to show it again.
        using var vm = new TideViewModel(new TideSettings(), () => { });

        Assert.True(vm.HideFromCapture);
        Assert.Equal("Show", vm.CaptureLabel);

        vm.HideFromCapture = false;
        Assert.Equal("Hide", vm.CaptureLabel);
        Assert.Contains("Visible", vm.CaptureHint);

        vm.HideFromCapture = true;
        Assert.Equal("Show", vm.CaptureLabel);
        Assert.Contains("Hidden", vm.CaptureHint);
    }

    [Fact]
    public void Speed_presets_are_a_single_selected_segment()
    {
        var settings = new TideSettings();
        using var vm = new TideViewModel(settings, () => { });

        Assert.Equal(1, vm.SpeedIndex);
        Assert.Equal(1, vm.Speed);

        vm.SpeedIndex = 0;
        Assert.Equal(0.6, vm.Speed);
        vm.SpeedIndex = 3;
        Assert.Equal(2.4, vm.Speed);
        Assert.Equal(2.4, settings.Speed);
    }

    [Fact]
    public void Saved_speed_selects_the_nearest_preset()
    {
        var settings = new TideSettings { Speed = 1.5 };
        using var vm = new TideViewModel(settings, () => { });

        Assert.Equal(2, vm.SpeedIndex);
        Assert.Equal(vm.Speeds[2], vm.Speeds[vm.SpeedIndex]);
    }
}
