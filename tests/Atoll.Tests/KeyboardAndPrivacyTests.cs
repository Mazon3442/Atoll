using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Atoll.Activities;
using Atoll.Modules;
using Atoll.Modules.Clock;
using Atoll.Modules.Privacy;
using Atoll.Theme;
using Atoll.Settings;
using Atoll.Shell;
using Microsoft.Win32;

namespace Atoll.Tests;

public class KeyboardAndPrivacyTests
{
    [Fact]
    public void Default_shortcuts_parse_to_global_hotkeys()
    {
        var settings = new AppSettings();
        Assert.True(HotkeyService.TryParse(settings.OpenShortcut, out var modifiers, out var key));
        Assert.Equal(0x2u | 0x1u, modifiers); // Ctrl + Alt
        Assert.Equal(0x20u, key);             // VK_SPACE
        Assert.True(HotkeyService.TryParse(settings.HideShortcut, out _, out _));
        Assert.False(HotkeyService.TryParse("", out _, out _));
        Assert.False(HotkeyService.TryParse("not a shortcut", out _, out _));
    }

    [Fact]
    public void Recorded_keys_need_a_real_modifier_except_function_keys()
    {
        Assert.Equal("Ctrl+Shift+K", HotkeyService.FromKeys(ModifierKeys.Control | ModifierKeys.Shift, Key.K));
        Assert.Null(HotkeyService.FromKeys(ModifierKeys.Shift, Key.K));    // would steal capital K
        Assert.Null(HotkeyService.FromKeys(ModifierKeys.None, Key.Space)); // would steal typing
        Assert.Null(HotkeyService.FromKeys(ModifierKeys.Control, Key.LeftCtrl));
        Assert.Equal("F9", HotkeyService.FromKeys(ModifierKeys.None, Key.F9));
        Assert.Equal("Ctrl + Alt + Space", HotkeyService.Describe("Ctrl+Alt+Space"));
        Assert.Equal("Win + Shift + A", HotkeyService.Describe(HotkeyService.FromKeys(ModifierKeys.Windows | ModifierKeys.Shift, Key.A)));
        Assert.Equal("Off", HotkeyService.Describe(""));
    }

    [Fact]
    public void Settings_refuse_the_same_shortcut_twice()
    {
        var service = new SettingsService(new SettingsStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json")));
        using var vm = new SettingsViewModel(service, []);
        vm.OpenShortcut = service.Current.HideShortcut;
        Assert.Equal("Ctrl+Alt+Space", service.Current.OpenShortcut);
        Assert.Contains("already the other shortcut", vm.ShortcutMessage);
        vm.OpenShortcut = "Ctrl+Shift+F12";
        Assert.Equal("Ctrl+Shift+F12", service.Current.OpenShortcut);
        Assert.Equal("", vm.ShortcutMessage);
        vm.HideShortcut = "";
        Assert.Equal("", service.Current.HideShortcut); // off is allowed
    }

    [Fact]
    public void Finds_apps_using_the_microphone_and_camera_now()
    {
        var path = @"Software\AtollTests\" + Guid.NewGuid().ToString("N");
        try
        {
            using (var store = Registry.CurrentUser.CreateSubKey(path))
            {
                Use(store, @"microphone\Microsoft.WindowsSoundRecorder_8wekyb3d8bbwe", start: 10, stop: 0);
                Use(store, @"microphone\NonPackaged\C:#Missing#Discord.exe", start: 10, stop: 0);
                Use(store, @"microphone\NonPackaged\C:#Missing#Zoom.exe", start: 10, stop: 20); // finished
                Use(store, @"webcam\Microsoft.WindowsCamera_8wekyb3d8bbwe", start: 10, stop: 0);
                Use(store, @"webcam\NonPackaged\C:#Missing#Never.exe", start: 0, stop: 0);   // never used
            }
            using var read = Registry.CurrentUser.OpenSubKey(path)!;
            Assert.Equal(["Discord", "Windows Sound Recorder"], PrivacyModule.AppsUsing(read, "microphone"));
            Assert.Equal(["Windows Camera"], PrivacyModule.AppsUsing(read, "webcam"));
            Assert.Empty(PrivacyModule.AppsUsing(read, "location"));
        }
        finally { Registry.CurrentUser.DeleteSubKeyTree(path, throwOnMissingSubKey: false); }
    }

    [Theory]
    [InlineData("MSTeams_8wekyb3d8bbwe", "MS Teams")]
    [InlineData("Microsoft.WindowsCamera_8wekyb3d8bbwe", "Windows Camera")]
    [InlineData("5319275A.WhatsAppDesktop_cv1g1gvanyjgm", "Whats App Desktop")]
    public void Names_store_apps_readably(string family, string name) => Assert.Equal(name, PrivacyModule.PackagedAppName(family));

    [Fact]
    public void Privacy_state_describes_what_is_in_use()
    {
        var state = new PrivacyViewModel { MicrophoneApps = ["Discord"] };
        Assert.Equal("Microphone in use: Discord", state.Description);
        state.CameraApps = ["Discord", "Teams"];
        Assert.True(state.Camera && state.Microphone);
        Assert.Equal("Camera and microphone in use", state.Title);
        Assert.Equal("Discord · Teams", state.Apps);
    }

    private static void Use(RegistryKey store, string app, long start, long stop)
    {
        using var key = store.CreateSubKey(app);
        key.SetValue("LastUsedTimeStart", start, RegistryValueKind.QWord);
        key.SetValue("LastUsedTimeStop", stop, RegistryValueKind.QWord);
    }
}

/// <summary>Runs on the shared WPF thread (see MediaSeekTests): the island's keyboard handling, privacy dots and notch shape.</summary>
internal static class KeyboardIslandUi
{
    internal static void Verify()
    {
        var service = new SettingsService(new SettingsStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json")));
        service.Current.Theme.Motion = MotionStyle.Off; // sizes apply at once, so renders show the final shape
        foreach (var dictionary in new[] { "Modules/Privacy/PrivacyTemplates.xaml", "Modules/Clock/ClockTemplates.xaml" })
            Application.Current.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri($"/Atoll;component/{dictionary}", UriKind.Relative) });
        ThemeService.Apply(service.Current.Theme);
        using var host = new ActivityHost();
        using var manager = new ModuleManager([], host, Application.Current.Dispatcher, service);
        foreach (var tab in PanelGroups.Combine(GroupedPanelsTests.Modules())) manager.Tabs.Add(tab);
        var island = new IslandWindow(Monitors.GetAll()[0], service, manager, _ => { });
        try
        {
            var privacy = new PrivacyViewModel { MicrophoneApps = ["Discord"], CameraApps = ["Teams"] };
            var timer = new TimerViewModel(TimeProvider.System);
            timer.StartCountdownCommand.Execute(5);
            island.SetActivities(new Activity("privacy.inuse", "privacy", privacy, ActivityPriority.Normal), null);
            island.Show();
            Drain(); island.UpdateLayout();
            var pill = (PillBorder)island.FindName("Pill");
            var content = (ContentPresenter)island.FindName("PillContent");
            Assert.Same(Templates.Find(island, privacy, "Compact"), content.ContentTemplate);
            Render(pill, "privacy-compact");

            // Settled to a dot beside the timer.
            island.SetActivities(new Activity("clock.timer", "clock", timer, ActivityPriority.Normal),
                new Activity("privacy.inuse", "privacy", privacy, ActivityPriority.Ambient));
            Drain(); island.UpdateLayout();
            var bubble = (PillBorder)island.FindName("Bubble");
            Assert.True(bubble.IsVisible);
            Render(bubble, "privacy-bubble");

            // Notch: flush with the top, square top corners.
            service.Current.Theme.Shape = IslandShape.Notch;
            island.ApplySettings();
            Drain(); island.UpdateLayout();
            Assert.Equal(0, ((Grid)island.FindName("Stage")).Margin.Top);
            Assert.Equal(new CornerRadius(0, 0, pill.Radius, pill.Radius), pill.CornerRadius);
            Render(pill, "island-notch");

            // The open shortcut opens with focus on the tabs; Ctrl+Tab wraps, the wheel stops at the ends.
            var tabs = (ListBox)island.FindName("Tabs");
            island.ToggleFromKeyboard();
            Drain();
            Assert.True(island.IsExpanded);
            Assert.True(tabs.IsKeyboardFocusWithin);
            tabs.SelectedIndex = 0;
            island.StepTab(1, wrap: false);
            Assert.Equal(1, tabs.SelectedIndex);
            island.StepTab(-5, wrap: false);
            Assert.Equal(0, tabs.SelectedIndex);
            island.StepTab(-1, wrap: true);
            Assert.Equal(tabs.Items.Count - 1, tabs.SelectedIndex);
            Render(pill, "island-notch-open");
            island.ToggleFromKeyboard();
            Assert.False(island.IsExpanded);
        }
        finally { island.Close(); }
    }

    private static void Render(FrameworkElement element, string name)
    {
        // A VisualBrush leaves out the element's own offset (the side bubbles are shifted with a RenderTransform).
        var size = new Rect(0, 0, element.ActualWidth, element.ActualHeight);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen()) dc.DrawRectangle(new VisualBrush(element), null, size);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(size.Width), (int)Math.Ceiling(size.Height), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        var dir = Path.Combine(Path.GetTempPath(), "atoll-tier-one-review");
        Directory.CreateDirectory(dir);
        using var output = File.Create(Path.Combine(dir, name + ".png")); encoder.Save(output);
    }

    private static void Drain()
    {
        var frame = new DispatcherFrame();
        Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
    }
}
