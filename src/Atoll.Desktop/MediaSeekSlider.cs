using Atoll.Modules.Media;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Atoll.Desktop;

public sealed class MediaSeekSlider : Slider
{
    protected override Type StyleKeyOverride => typeof(Slider);
    private Track? _track;
    private IPointer? _pointer;
    private MediaViewModel? _model;
    private int _revision;
    private double _offset;
    private bool _active;

    public MediaSeekSlider()
    {
        AddHandler(PointerPressedEvent, Pressed, RoutingStrategies.Tunnel, true);
        AddHandler(PointerMovedEvent, Moved, RoutingStrategies.Tunnel, true);
        AddHandler(PointerReleasedEvent, Released, RoutingStrategies.Tunnel, true);
        AddHandler(KeyDownEvent, KeyStarted, RoutingStrategies.Tunnel, true);
        AddHandler(KeyUpEvent, KeyFinished, RoutingStrategies.Bubble, true);
        PointerCaptureLost += (_, _) => { if (_active && _pointer is not null) Cancel(); };
        LostFocus += (_, _) => Cancel();
        DetachedFromVisualTree += (_, _) => Cancel();
        DataContextChanged += (_, _) => Cancel();
    }
    protected override void OnApplyTemplate(TemplateAppliedEventArgs e) { base.OnApplyTemplate(e); _track = e.NameScope.Find<Track>("PART_Track"); }
    private bool Begin()
    {
        if (DataContext is not MediaViewModel { CanSeek: true } vm || !IsEnabled) return false;
        if (!_active) { _model = vm; _revision = vm.PlaybackRevision; _active = true; vm.IsScrubbing = true; }
        return true;
    }
    private void Pressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || !Begin()) return;
        Focus(); _pointer = e.Pointer;
        _offset = 0;
        if (_track?.Thumb is { } thumb)
        {
            var point = e.GetPosition(thumb);
            if (new Rect(thumb.Bounds.Size).Contains(point)) _offset = point.X - thumb.Bounds.Width / 2;
        }
        e.Pointer.Capture(this); Pick(e.GetPosition(_track ?? (Visual)this)); e.Handled = true;
    }
    private void Moved(object? sender, PointerEventArgs e)
    {
        if (_active && ReferenceEquals(_pointer, e.Pointer)) { Pick(e.GetPosition(_track ?? (Visual)this)); e.Handled = true; }
    }
    private async void Released(object? sender, PointerReleasedEventArgs e)
    {
        if (!_active || !ReferenceEquals(_pointer, e.Pointer)) return;
        Pick(e.GetPosition(_track ?? (Visual)this)); e.Handled = true; await Commit();
    }
    private void Pick(Point point)
    {
        var thumb = _track?.Thumb?.Bounds.Width ?? 12;
        var width = _track?.Bounds.Width ?? Bounds.Width;
        var fraction = Math.Clamp((point.X - _offset - thumb / 2) / Math.Max(1, width - thumb), 0, 1);
        if (IsDirectionReversed) fraction = 1 - fraction;
        SetCurrentValue(ValueProperty, Minimum + (Maximum - Minimum) * fraction);
    }
    private static bool SeekKey(Key key) => key is Key.Left or Key.Right or Key.Up or Key.Down or Key.Home or Key.End or Key.PageUp or Key.PageDown;
    private void KeyStarted(object? sender, KeyEventArgs e) { if (SeekKey(e.Key)) Begin(); }
    private async void KeyFinished(object? sender, KeyEventArgs e) { if (_active && SeekKey(e.Key)) { e.Handled = true; await Commit(); } }
    private async Task Commit()
    {
        var vm = _model; var value = Value; var revision = _revision;
        End();
        if (vm is null) return;
        try { if (ReferenceEquals(DataContext, vm) && revision == vm.PlaybackRevision && vm.SeekCommand.CanExecute(value)) await vm.SeekCommand.ExecuteAsync(value); }
        finally { if (ReferenceEquals(DataContext, vm)) SetCurrentValue(ValueProperty, vm.Progress); }
    }
    private void End()
    {
        _active = false;
        if (_model is { } vm) vm.IsScrubbing = false;
        var pointer = _pointer; _pointer = null; pointer?.Capture(null);
    }
    private void Cancel() { var vm = _model; End(); if (vm is not null && ReferenceEquals(DataContext, vm)) SetCurrentValue(ValueProperty, vm.Progress); _model = null; }
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e) { }
}
