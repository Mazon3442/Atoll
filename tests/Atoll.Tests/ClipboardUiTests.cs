using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Atoll.Modules.Clipboard;

namespace Atoll.Tests;

/// <summary>Runs on the shared WPF thread (see MediaSeekTests): the clipboard rows render and take keyboard focus.</summary>
internal static class ClipboardUi
{
    internal static void Verify()
    {
        var history = new ClipboardHistory();
        for (var i = 0; i < 3; i++)
            history.Add(new ClipItem(ClipKind.Text, _ => { }, item => history.Remove(item)) { Text = $"Copied note {i}" });
        var vm = new ClipboardViewModel(history);
        vm.Configure(enabled: true);

        var panel = (FrameworkElement)((DataTemplate)Application.Current.Resources["ClipboardViewModel.Panel"]).LoadContent();
        panel.DataContext = vm;
        panel.Width = 460;
        panel.Height = 220;
        var window = new Window
        {
            Width = 460, Height = 220, Content = panel, ShowInTaskbar = false,
            WindowStyle = WindowStyle.None, AllowsTransparency = true,
            Background = Brushes.Black, Foreground = Brushes.White,
            FontFamily = (FontFamily)Application.Current.Resources["Island.Font"],
        };
        window.Show();
        try
        {
            window.Activate();
            window.UpdateLayout();
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Loaded);

            var rows = Descendants<Button>(panel).Where(b => b.DataContext is ClipItem && b.Focusable).ToList();
            Assert.Equal(3, rows.Count);
            Assert.All(rows, row =>
            {
                Assert.True(row.Focusable);
                Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(row)));
                Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetHelpText(row)));
            });

            // Up and Down walk the rows; Delete removes the focused copy.
            Assert.True(rows[0].Focus());
            Press(rows[0], Key.Down);
            Assert.True(rows[1].IsKeyboardFocused);
            Press(rows[1], Key.Up);
            Assert.True(rows[0].IsKeyboardFocused);
            Press(rows[0], Key.Delete);
            Assert.Equal(2, history.Items.Count);

            var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(window);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            var directory = Path.Combine(Path.GetTempPath(), "atoll-panel-review");
            Directory.CreateDirectory(directory);
            using var output = File.Create(Path.Combine(directory, "clipboard-focused.png"));
            encoder.Save(output);
        }
        finally { window.Close(); }
    }

    private static void Press(UIElement element, Key key)
    {
        var args = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(element)!, 0, key)
        {
            RoutedEvent = Keyboard.PreviewKeyDownEvent,
        };
        element.RaiseEvent(args);
        Assert.True(args.Handled);
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }
}
