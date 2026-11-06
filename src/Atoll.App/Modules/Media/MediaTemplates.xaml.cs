using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace Atoll.Modules.Media;

public partial class MediaTemplates : ResourceDictionary
{
    public MediaTemplates() => InitializeComponent();

    private void SeekStarted(object sender, DragStartedEventArgs e)
    {
        if (((Slider)sender).DataContext is MediaViewModel vm) vm.IsScrubbing = true;
    }

    private async void SeekCompleted(object sender, DragCompletedEventArgs e)
    {
        var slider = (Slider)sender;
        if (slider.DataContext is not MediaViewModel vm) return;
        vm.IsScrubbing = false;
        if (!e.Canceled) await CommitSeek(slider);
        else slider.GetBindingExpression(Slider.ValueProperty)?.UpdateTarget();
    }

    private async void SeekClicked(object sender, MouseButtonEventArgs e)
    {
        var slider = (Slider)sender;
        // Thumb drags commit in DragCompleted, after this preview event.
        if (slider.DataContext is MediaViewModel { IsScrubbing: false }) await CommitSeek(slider);
    }

    private async void SeekKeyReleased(object sender, KeyEventArgs e)
    {
        if (!IsSeekKey(e.Key)) return;
        var slider = (Slider)sender;
        if (slider.DataContext is MediaViewModel vm) vm.IsScrubbing = false;
        await CommitSeek(slider);
    }

    private void SeekKeyPressed(object sender, KeyEventArgs e)
    {
        if (IsSeekKey(e.Key) && ((Slider)sender).DataContext is MediaViewModel vm) vm.IsScrubbing = true;
    }

    private void SeekFocusLost(object sender, KeyboardFocusChangedEventArgs e)
    {
        var slider = (Slider)sender;
        if (slider.DataContext is MediaViewModel vm) vm.IsScrubbing = false;
        slider.GetBindingExpression(Slider.ValueProperty)?.UpdateTarget();
    }

    private static bool IsSeekKey(Key key) =>
        key is Key.Left or Key.Right or Key.Up or Key.Down or Key.Home or Key.End or Key.PageUp or Key.PageDown;

    private static async Task CommitSeek(Slider slider)
    {
        if (slider.DataContext is MediaViewModel vm && vm.SeekCommand.CanExecute(slider.Value))
            await vm.SeekCommand.ExecuteAsync(slider.Value);
        slider.GetBindingExpression(Slider.ValueProperty)?.UpdateTarget();
    }
}
