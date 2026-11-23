using Atoll.Modules.Launcher;

namespace Atoll.Tests;

public class PinLayoutTests
{
    private static PinnedLink Pin(string name, string group = "") => new() { Name = name, Target = "x", Group = group };

    [Fact]
    public void Loose_pins_keep_their_order_and_a_group_takes_its_first_pins_place()
    {
        var pins = new[] { Pin("Mail"), Pin("Steam", "Games"), Pin("Docs"), Pin("Epic", "games "), Pin("Notes") };

        var slots = PinLayout.Build(pins);

        Assert.Equal(["Mail", "Games", "Docs", "Notes"], slots.Select(slot => slot.Name));
        Assert.Equal(["Steam", "Epic"], slots[1].Pins.Select(pin => pin.Name));
        Assert.True(slots[1].IsGroup);
        Assert.False(slots[0].IsGroup);
        Assert.Equal("Mail", slots[0].Single.Name);
    }

    [Fact]
    public void A_group_of_one_is_still_a_group_so_it_can_grow()
    {
        var slot = Assert.Single(PinLayout.Build([Pin("Steam", "Games")]));
        Assert.True(slot.IsGroup);
        Assert.Equal("Games", slot.Name);
    }

    [Theory]
    [InlineData(@"C:\Program Files\App\app.exe", true)]
    [InlineData("https://example.com", true)]
    [InlineData("steam://run/570", true)]
    [InlineData("mailto:me@example.com", true)]
    [InlineData(@"\\server\share\file.txt", true)]
    [InlineData("%LOCALAPPDATA%\\app.exe", true)]
    [InlineData("\"C:\\a b\\c.exe\"", true)]
    [InlineData("hello world", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Only_openable_targets_are_accepted(string? target, bool expected) =>
        Assert.Equal(expected, PinLayout.IsOpenable(target));

    [Theory]
    [InlineData("https://www.example.com/page", "Example.com")]
    [InlineData("steam://run/570", "Steam")]
    [InlineData(@"C:\Tools\Notepad++.exe", "Notepad++")]
    public void A_missing_name_is_suggested_from_the_target(string target, string expected) =>
        Assert.Equal(expected, PinLayout.SuggestName(target));
}

public class PinnedLauncherTests
{
    private static (PinnedViewModel Vm, List<PinnedLink> Pins, int Saves) Make(params PinnedLink[] pins)
    {
        var list = pins.ToList();
        var saves = 0;
        var vm = new PinnedViewModel(list, () => new Atoll.Settings.AppSettings(), () => saves++);
        return (vm, list, saves);
    }

    [Fact]
    public void A_saved_form_pins_a_link_and_names_it_when_no_name_is_typed()
    {
        var (vm, pins, _) = Make();
        vm.StartAddCommand.Execute(null);
        vm.FormTarget = "steam://run/570";
        vm.SaveFormCommand.Execute(null);

        var pin = Assert.Single(pins);
        Assert.Equal(("Steam", "steam://run/570"), (pin.Name, pin.Target));
        Assert.False(vm.FormOpen);
        Assert.Single(vm.Slots);
    }

    [Fact]
    public void A_target_that_cannot_be_opened_keeps_the_form_open_with_a_reason()
    {
        var (vm, pins, _) = Make();
        vm.StartAddCommand.Execute(null);
        vm.FormTarget = "hello world";
        vm.SaveFormCommand.Execute(null);

        Assert.Empty(pins);
        Assert.True(vm.FormOpen);
        Assert.False(string.IsNullOrEmpty(vm.FormError));
    }

    [Fact]
    public void A_hotkey_must_be_a_real_shortcut_and_not_one_already_used()
    {
        var (vm, pins, _) = Make(new PinnedLink { Name = "Mail", Target = "https://mail.example.com", Hotkey = "Ctrl+Alt+1" });
        vm.StartAddCommand.Execute(null);
        vm.FormTarget = "https://example.com";

        vm.FormHotkey = "not a shortcut";
        vm.SaveFormCommand.Execute(null);
        Assert.Single(pins);

        vm.FormHotkey = "Ctrl+Alt+1"; // another pin has it
        vm.SaveFormCommand.Execute(null);
        Assert.Single(pins);
        Assert.Contains("already", vm.FormError);

        vm.FormHotkey = "Ctrl+Alt+Space"; // the open shortcut
        vm.SaveFormCommand.Execute(null);
        Assert.Single(pins);

        vm.FormHotkey = "Ctrl+Alt+2";
        vm.SaveFormCommand.Execute(null);
        Assert.Equal(2, pins.Count);
    }

    [Fact]
    public void Editing_keeps_a_pins_own_hotkey_valid_and_groups_form_chips()
    {
        var mail = new PinnedLink { Name = "Mail", Target = "https://mail.example.com", Hotkey = "Ctrl+Alt+1" };
        var (vm, pins, _) = Make(mail);

        vm.EditCommand.Execute(vm.Slots[0].Chips[0]);
        vm.FormGroup = "Work";
        vm.SaveFormCommand.Execute(null); // its own hotkey is not a conflict

        Assert.Single(pins);
        Assert.Equal("Work", mail.Group);
        Assert.True(vm.Slots[0].IsGroup);

        vm.Slots[0].ClickCommand.Execute(null); // a group opens its chips
        Assert.True(vm.HasOpenGroup);
        Assert.Equal("Mail", Assert.Single(vm.OpenChips).Name);
        vm.Slots[0].ClickCommand.Execute(null);
        Assert.False(vm.HasOpenGroup);
    }

    [Fact]
    public void Removing_a_chip_unpins_it_and_a_start_menu_app_is_pinned_once()
    {
        var (vm, pins, _) = Make(new PinnedLink { Name = "Mail", Target = "https://mail.example.com" });
        var app = new Atoll.Modules.Launcher.LauncherApp("Notepad", @"C:\Windows\notepad.exe", "Start Menu");

        vm.PinApp(app);
        vm.PinApp(app);
        Assert.Equal(2, pins.Count);

        vm.RemoveCommand.Execute(vm.Slots[0].Chips[0]);
        Assert.Equal("Notepad", Assert.Single(pins).Name);
    }
}
