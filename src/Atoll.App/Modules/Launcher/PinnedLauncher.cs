using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Media;
using Atoll.Settings;
using Atoll.Shell;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Atoll.Modules.Launcher;

/// <summary>A pinned shortcut as a chip: its name and the program's own icon when the target is a file.</summary>
public sealed class PinChip(PinnedLink pin)
{
    public PinnedLink Pin { get; } = pin;
    public string Name => Pin.Name;
    public string Initial => Pin.Name.Length > 0 ? Pin.Name[..1].ToUpper(System.Globalization.CultureInfo.CurrentCulture) : "?";

    /// <summary>The icon of the program or file; links and protocols show the initial instead, so nothing is fetched from a site.</summary>
    public ImageSource? Icon { get; } = IconFor(pin.Target);

    private static ImageSource? IconFor(string target)
    {
        var path = Environment.ExpandEnvironmentVariables(target.Trim().Trim('"'));
        return path.Length > 2 && (path[1] == ':' || path.StartsWith(@"\\", StringComparison.Ordinal)) && File.Exists(path) ? ShellIcons.For(path) : null;
    }
}

/// <summary>One place in the pinned row: a single shortcut, or a group that opens a second row of them.</summary>
public sealed partial class PinSlotViewModel(PinSlot slot, Action<PinSlotViewModel> click) : ObservableObject
{
    public PinSlot Slot { get; } = slot;
    public string Name => Slot.Name;
    public bool IsGroup => Slot.IsGroup;
    public string Label => IsGroup ? $"{Name} ({Slot.Pins.Count})" : Name;
    public IReadOnlyList<PinChip> Chips { get; } = slot.Pins.Select(pin => new PinChip(pin)).ToList();
    public PinChip? Single => IsGroup ? null : Chips[0];
    public string Initial => Chips[0].Initial;

    [ObservableProperty] private bool _isOpen;

    [RelayCommand] private void Click() => click(this);
}

/// <summary>
/// Your own shortcuts at the top of the Launcher: apps, files, sites and links such as steam://run/..., each with an
/// optional hotkey that opens it from anywhere. Shortcuts with the same group name share one chip.
/// </summary>
public sealed partial class PinnedViewModel : ObservableObject
{
    private readonly List<PinnedLink> _pins;
    private readonly Func<AppSettings> _settings;
    private readonly Action _save;
    private PinnedLink? _editing;

    public PinnedViewModel(List<PinnedLink> pins, Func<AppSettings> settings, Action save)
    {
        _pins = pins;
        _settings = settings;
        _save = save;
        Rebuild();
    }

    public ObservableCollection<PinSlotViewModel> Slots { get; } = [];
    public ObservableCollection<PinChip> OpenChips { get; } = [];

    public bool HasPins => _pins.Count > 0;
    public bool HasOpenGroup => OpenChips.Count > 0;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(FormTitle))] private bool _formOpen;
    [ObservableProperty] private string _formName = "";
    [ObservableProperty] private string _formTarget = "";
    [ObservableProperty] private string _formGroup = "";
    [ObservableProperty] private string _formHotkey = "";
    [ObservableProperty] private string _formError = "";
    [ObservableProperty] private string _openGroupName = "";

    public string FormTitle => _editing is null ? "Pin a shortcut" : "Edit shortcut";

    /// <summary>Raised after pins change, so the module can register hotkeys.</summary>
    public event Action? Changed;

    public IReadOnlyList<PinnedLink> Pins => _pins;

    private void Rebuild()
    {
        var openGroup = Slots.FirstOrDefault(slot => slot.IsOpen)?.Name;
        Slots.Clear();
        foreach (var slot in PinLayout.Build(_pins))
            Slots.Add(new PinSlotViewModel(slot, OnSlotClicked) { IsOpen = slot.IsGroup && slot.Name.Equals(openGroup, StringComparison.OrdinalIgnoreCase) });
        ShowOpenGroup(Slots.FirstOrDefault(slot => slot.IsOpen));
        OnPropertyChanged(nameof(HasPins));
    }

    private void ShowOpenGroup(PinSlotViewModel? open)
    {
        OpenChips.Clear();
        if (open is not null) foreach (var chip in open.Chips) OpenChips.Add(chip);
        OpenGroupName = open?.Name ?? "";
        OnPropertyChanged(nameof(HasOpenGroup));
    }

    private void OnSlotClicked(PinSlotViewModel slot)
    {
        if (!slot.IsGroup) { Launch(slot.Chips[0].Pin); return; }
        var open = !slot.IsOpen;
        foreach (var other in Slots) other.IsOpen = false;
        slot.IsOpen = open;
        ShowOpenGroup(open ? slot : null);
    }

    [RelayCommand]
    internal void Launch(PinnedLink? pin)
    {
        if (pin is null) return;
        try { using var process = Process.Start(new ProcessStartInfo(Environment.ExpandEnvironmentVariables(pin.Target.Trim().Trim('"'))) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Log.Error($"Could not open {pin.Name}", ex);
        }
    }

    /// <summary>Pins a Start Menu app from the list below, without opening the form.</summary>
    public void PinApp(LauncherApp app)
    {
        if (_pins.Any(pin => pin.Target.Equals(app.LaunchPath, StringComparison.OrdinalIgnoreCase))) return;
        _pins.Add(new PinnedLink { Name = app.Name, Target = app.LaunchPath });
        Commit();
    }

    [RelayCommand]
    private void StartAdd()
    {
        _editing = null;
        FormName = FormTarget = FormGroup = FormHotkey = FormError = "";
        FormOpen = true;
        OnPropertyChanged(nameof(FormTitle));
    }

    [RelayCommand]
    private void Edit(PinChip? chip)
    {
        if (chip is null) return;
        _editing = chip.Pin;
        FormName = chip.Pin.Name;
        FormTarget = chip.Pin.Target;
        FormGroup = chip.Pin.Group;
        FormHotkey = chip.Pin.Hotkey;
        FormError = "";
        FormOpen = true;
        OnPropertyChanged(nameof(FormTitle));
    }

    [RelayCommand]
    private void Remove(PinChip? chip)
    {
        if (chip is null) return;
        _pins.Remove(chip.Pin);
        if (ReferenceEquals(_editing, chip.Pin)) FormOpen = false;
        Commit();
    }

    [RelayCommand] private void CancelForm() => FormOpen = false;

    [RelayCommand]
    private void SaveForm()
    {
        var target = FormTarget.Trim();
        if (!PinLayout.IsOpenable(target)) { FormError = "Enter a path, a web address, or a link such as steam://run/570."; return; }
        var hotkey = FormHotkey.Trim();
        if (hotkey.Length > 0 && !HotkeyService.TryParse(hotkey, out _, out _)) { FormError = "That is not a shortcut. Try Ctrl+Alt+1."; return; }
        if (hotkey.Length > 0 && IsHotkeyTaken(hotkey)) { FormError = $"{HotkeyService.Describe(hotkey)} is already another shortcut."; return; }

        var name = FormName.Trim().Length > 0 ? FormName.Trim() : PinLayout.SuggestName(target);
        if (_editing is { } pin)
        {
            pin.Name = name;
            pin.Target = target;
            pin.Group = FormGroup.Trim();
            pin.Hotkey = hotkey;
        }
        else _pins.Add(new PinnedLink { Name = name, Target = target, Group = FormGroup.Trim(), Hotkey = hotkey });
        FormOpen = false;
        Commit();
    }

    private bool IsHotkeyTaken(string hotkey)
    {
        var s = _settings();
        var taken = new List<string> { s.OpenShortcut, s.HideShortcut, s.PaletteShortcut };
        taken.AddRange(ShortcutCatalog.All.Select(def => ShortcutCatalog.Resolve(s, def.Id)));
        taken.AddRange(_pins.Where(pin => !ReferenceEquals(pin, _editing)).Select(pin => pin.Hotkey));
        return taken.Any(other => other.Length > 0 && string.Equals(other, hotkey, StringComparison.OrdinalIgnoreCase));
    }

    private void Commit()
    {
        Rebuild();
        _save();
        Changed?.Invoke();
    }
}
