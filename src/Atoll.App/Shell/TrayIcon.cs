using System.Windows.Controls;
using H.NotifyIcon;

namespace Atoll.Shell;

public sealed class TrayIcon : IDisposable
{
    private readonly TaskbarIcon _icon;
    private readonly MenuItem _pauseItem;
    private readonly MenuItem _focusItem;

    public TrayIcon(Action openSettings, Action<bool> setPaused, Action<bool> setFocus, Action quit)
    {
        var settingsItem = new MenuItem { Header = "Settings" };
        settingsItem.Click += (_, _) => openSettings();

        var pauseItem = _pauseItem = new MenuItem { Header = "Hide island", IsCheckable = true };
        pauseItem.Click += (_, _) => setPaused(pauseItem.IsChecked);

        var focusItem = _focusItem = new MenuItem { Header = "Do Not Disturb", IsCheckable = true };
        focusItem.Click += (_, _) => setFocus(focusItem.IsChecked);

        var quitItem = new MenuItem { Header = "Quit" };
        quitItem.Click += (_, _) => quit();

        var menu = new ContextMenu();
        menu.Items.Add(settingsItem);
        menu.Items.Add(pauseItem);
        menu.Items.Add(focusItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(quitItem);

        _icon = new TaskbarIcon
        {
            ToolTipText = "ATOLL",
            Icon = LoadIcon(),
            ContextMenu = menu,
            NoLeftClickDelay = true,
        };
        _icon.TrayLeftMouseDown += (_, _) => openSettings();
        _icon.ForceCreate(enablesEfficiencyMode: false);
    }

    // The exe's own icon, at the tray's size so Windows picks the simplified small variant
    // instead of shrinking the large one.
    private static System.Drawing.Icon? LoadIcon()
    {
        var size = Native.GetSystemMetrics(Native.SM_CXSMICON);
        return Environment.ProcessPath is { } exe
            ? System.Drawing.Icon.ExtractIcon(exe, 0, size > 0 ? size : 16)
            : null;
    }

    /// <summary>Keeps the "Hide island" check in step when the hide shortcut changes it.</summary>
    public bool Paused { set => _pauseItem.IsChecked = value; }

    /// <summary>Keeps the Do Not Disturb check in step with the focus service.</summary>
    public bool Focus { set => _focusItem.IsChecked = value; }

    public void Dispose() => _icon.Dispose();
}
