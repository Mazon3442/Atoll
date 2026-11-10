using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Atoll.Modules.Tide.Listen;

namespace Atoll.Modules.Tide;

public partial class TideTemplates : ResourceDictionary
{
    private TideViewModel? _vm;
    private ScrollViewer? _scroll;
    private ListBox? _transcript;
    private ListenViewModel? _listen;

    public TideTemplates() => InitializeComponent();

    private void OnTranscriptLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not ListBox list || list.DataContext is not TideViewModel vm) return;
        DetachTranscript();
        _transcript = list;
        _listen = vm.Listen;
        _listen.Transcript.CollectionChanged += OnTranscriptChanged;
        ScrollTranscriptToEnd();
    }

    private void OnTranscriptUnloaded(object sender, RoutedEventArgs e) => DetachTranscript();

    private void OnTranscriptChanged(object? sender, NotifyCollectionChangedEventArgs e) => ScrollTranscriptToEnd();

    /// <summary>Keeps the newest line in view without stealing focus from the notes box.</summary>
    private void ScrollTranscriptToEnd()
    {
        if (_transcript is null || _listen is null || _listen.Transcript.Count == 0) return;
        _transcript.ScrollIntoView(_listen.Transcript[^1]);
    }

    private void DetachTranscript()
    {
        if (_listen is not null) _listen.Transcript.CollectionChanged -= OnTranscriptChanged;
        _listen = null;
        _transcript = null;
    }

    private void OnPromptLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not ScrollViewer scroll || scroll.DataContext is not TideViewModel vm) return;
        Detach();
        _scroll = scroll;
        _vm = vm;
        vm.PropertyChanged += OnViewModelChanged;
        vm.MaxOffset = scroll.ScrollableHeight;
        scroll.ScrollToVerticalOffset(vm.Offset);
    }

    private void OnPromptUnloaded(object sender, RoutedEventArgs e) => Detach();

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_vm is null || _scroll is null) return;
        if (e.PropertyName == nameof(TideViewModel.Offset))
            _scroll.ScrollToVerticalOffset(_vm.Offset);
    }

    private void OnPromptScroll(object sender, ScrollChangedEventArgs e)
    {
        if (_vm is not null && _scroll is not null) _vm.MaxOffset = _scroll.ScrollableHeight;
    }

    private void OnPromptWheel(object sender, MouseWheelEventArgs e)
    {
        if (_vm is null) return;
        _vm.ScrollBy(-e.Delta * 0.5);
        e.Handled = true;
    }

    private void Detach()
    {
        if (_vm is not null) _vm.PropertyChanged -= OnViewModelChanged;
        _vm = null;
        _scroll = null;
    }
}
