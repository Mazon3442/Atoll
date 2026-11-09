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

    private readonly ConditionalWeakTable<Border, Action> _lyricsDetach = new();

    /// <summary>Keeps the sung line centered in the lyrics box, sliding smoothly to each new line.</summary>
    private void LyricsLoaded(object sender, RoutedEventArgs e)
    {
        var box = (Border)sender;
        LyricsUnloaded(box, e);
        if (box.DataContext is not MediaViewModel vm || box.Child is not Canvas { Children: [ItemsControl list] }) return;
        // A transform declared in the template is frozen, so animation or setting Y throws. Own one here.
        var shift = new System.Windows.Media.TranslateTransform();
        list.RenderTransform = shift;
        var queued = false;
        var retries = 0;

        void Follow(bool animate)
        {
            if (!box.IsLoaded || box.ActualHeight <= 0 || list.Items.Count == 0) return;
            list.UpdateLayout(); // containers exist only after a layout pass
            var index = Math.Clamp(vm.LyricIndex, 0, list.Items.Count - 1);
            if (list.ItemContainerGenerator.ContainerFromIndex(index) is not FrameworkElement line)
            {
                retries++;
                if (retries <= 3) Queue();
                return;
            }
            retries = 0;
            var point = line.TranslatePoint(new Point(), list);
            var target = box.ActualHeight * 0.5 - point.Y - line.ActualHeight / 2;
            // A seek can cross many lines; snap rather than sliding the whole song past the box.
            var far = Math.Abs(target - shift.Y) > box.ActualHeight * 2;
            if (animate && !far && SystemParameters.ClientAreaAnimation)
                shift.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, new System.Windows.Media.Animation.DoubleAnimation(target, TimeSpan.FromMilliseconds(420))
                { EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut } });
            else
            {
                shift.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, null);
                shift.Y = target;
            }
        }

        void Queue()
        {
            if (queued) return;
            queued = true;
            box.Dispatcher.BeginInvoke(() =>
            {
                queued = false;
                if (box.IsLoaded) Follow(true);
            }, System.Windows.Threading.DispatcherPriority.Loaded);
        }

        void Changed(object? s, System.ComponentModel.PropertyChangedEventArgs a)
        {
            if (a.PropertyName == nameof(MediaViewModel.LyricIndex))
                Queue();
        }
        // New song, new lyric list, or the box was shown/resized: jump, don't slide.
        void Resized(object s, SizeChangedEventArgs a) { retries = 0; Follow(false); }
        vm.PropertyChanged += Changed;
        list.SizeChanged += Resized;
        box.SizeChanged += Resized;
        _lyricsDetach.AddOrUpdate(box, () =>
        {
            vm.PropertyChanged -= Changed;
            list.SizeChanged -= Resized;
            box.SizeChanged -= Resized;
        });
        Queue();
    }

    private void LyricsUnloaded(object sender, RoutedEventArgs e)
    {
        if (_lyricsDetach.TryGetValue((Border)sender, out var detach)) { detach(); _lyricsDetach.Remove((Border)sender); }
    }
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
