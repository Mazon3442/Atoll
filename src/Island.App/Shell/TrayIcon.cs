using System.Windows.Controls;
using System.Windows.Media;
using H.NotifyIcon;

namespace Island.Shell;

public sealed class TrayIcon : IDisposable
{
    private readonly TaskbarIcon _icon;

    public TrayIcon(Action openSettings, Action<bool> setPaused, Action quit)
    {
        var settingsItem = new MenuItem { Header = "Settings" };
        settingsItem.Click += (_, _) => openSettings();

        var pauseItem = new MenuItem { Header = "Hide island", IsCheckable = true };
        pauseItem.Click += (_, _) => setPaused(pauseItem.IsChecked);

        var quitItem = new MenuItem { Header = "Quit" };
        quitItem.Click += (_, _) => quit();

        var menu = new ContextMenu();
        menu.Items.Add(settingsItem);
        menu.Items.Add(pauseItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(quitItem);

        _icon = new TaskbarIcon
        {
            ToolTipText = "Island",
            IconSource = new GeneratedIconSource
            {
                Text = "I",
                Foreground = Brushes.White,
                Background = Brushes.Black,
                FontWeight = System.Windows.FontWeights.Bold,
            },
            ContextMenu = menu,
            NoLeftClickDelay = true,
        };
        _icon.TrayLeftMouseDown += (_, _) => openSettings();
        _icon.ForceCreate(enablesEfficiencyMode: false);
    }

    public void Dispose() => _icon.Dispose();
}
