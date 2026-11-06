using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Runtime.CompilerServices;

namespace Atoll.Modules.Media;

public partial class MediaTemplates : ResourceDictionary
{
    private sealed class Gesture { public int Revision; public bool Active; public bool Dragging; public bool Attached; }
    private readonly ConditionalWeakTable<Slider, Gesture> _gestures = new();
    public MediaTemplates() => InitializeComponent();
    private void SeekLoaded(object sender, RoutedEventArgs e)
    {
        var slider = (Slider)sender; var gesture = _gestures.GetOrCreateValue(slider);
        if (gesture.Attached) return; gesture.Attached = true;
        slider.AddHandler(UIElement.PreviewMouseDownEvent, new MouseButtonEventHandler(SeekPointerPressed), true);
        slider.AddHandler(UIElement.PreviewMouseUpEvent, new MouseButtonEventHandler(SeekClicked), true);
    }

    private void Begin(Slider slider)
    {
        if (slider.DataContext is not MediaViewModel { CanSeek: true } vm) return;
        var gesture = _gestures.GetOrCreateValue(slider);
        if (!gesture.Active) { gesture.Active = true; gesture.Revision = vm.PlaybackRevision; }
        vm.IsScrubbing = true;
    }
    private void SeekPointerPressed(object sender, MouseButtonEventArgs e) { if (e.ChangedButton == MouseButton.Left) Begin((Slider)sender); }

    private void SeekStarted(object sender, DragStartedEventArgs e)
    {
        var slider = (Slider)sender; Begin(slider); _gestures.GetOrCreateValue(slider).Dragging = true;
    }

    private async void SeekCompleted(object sender, DragCompletedEventArgs e)
    {
        var slider = (Slider)sender;
        if (slider.DataContext is not MediaViewModel vm) return;
        if (!e.Canceled) await Finish(slider);
        else Cancel(slider);
    }

    private async void SeekClicked(object sender, MouseButtonEventArgs e)
    {
        var slider = (Slider)sender;
        // Thumb drags commit in DragCompleted, after this preview event.
        if (!_gestures.GetOrCreateValue(slider).Dragging) await Finish(slider);
    }

    private async void SeekKeyReleased(object sender, KeyEventArgs e)
    {
        if (!IsSeekKey(e.Key)) return;
        var slider = (Slider)sender;
        await Finish(slider);
    }

    private void SeekKeyPressed(object sender, KeyEventArgs e)
    {
        if (IsSeekKey(e.Key)) Begin((Slider)sender);
    }

    private void SeekFocusLost(object sender, KeyboardFocusChangedEventArgs e)
    {
        Cancel((Slider)sender);
    }

    private static bool IsSeekKey(Key key) =>
        key is Key.Left or Key.Right or Key.Up or Key.Down or Key.Home or Key.End or Key.PageUp or Key.PageDown;

    private async Task Finish(Slider slider)
    {
        var gesture = _gestures.GetOrCreateValue(slider);
        var revision = gesture.Revision; var active = gesture.Active;
        gesture.Active = false; gesture.Dragging = false;
        if (slider.DataContext is not MediaViewModel vm) return;
        vm.IsScrubbing = false;
        try { if (active && revision == vm.PlaybackRevision && vm.SeekCommand.CanExecute(slider.Value)) await vm.SeekCommand.ExecuteAsync(slider.Value); }
        finally { slider.GetBindingExpression(Slider.ValueProperty)?.UpdateTarget(); }
    }
    private void Cancel(Slider slider)
    {
        var gesture = _gestures.GetOrCreateValue(slider); gesture.Active = false; gesture.Dragging = false;
        if (slider.DataContext is MediaViewModel vm) vm.IsScrubbing = false;
        slider.GetBindingExpression(Slider.ValueProperty)?.UpdateTarget();
    }
}
