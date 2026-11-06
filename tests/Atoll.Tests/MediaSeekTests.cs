using Atoll.Modules.Media;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace Atoll.Tests;

public class MediaSeekTests
{
    [Theory]
    [InlineData(0, 30)]
    [InlineData(0.5, 120)]
    [InlineData(1, 210)]
    [InlineData(-1, 30)]
    [InlineData(2, 210)]
    public void Seek_uses_absolute_timeline_and_respects_seekable_range(double progress, double expected)
    {
        var target = MediaModule.SeekPosition(progress, TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(220),
            TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(210));
        Assert.Equal(TimeSpan.FromSeconds(expected), target);
    }

    [Theory]
    // Nothing shown yet: show whatever is current.
    [InlineData("media:firefox", false, null, false, true)]
    // A track change briefly makes a paused YouTube tab current: keep the music.
    [InlineData("media:firefox", false, "media:sidra", true, false)]
    // Another app starts playing: follow it.
    [InlineData("media:firefox", true, "media:sidra", true, true)]
    // The followed app is current again, or still current after the user picked another source: no change.
    [InlineData("media:sidra", true, "media:sidra", true, false)]
    [InlineData(null, false, "media:sidra", true, false)]
    public void Media_follows_only_a_newly_playing_player(string? candidate, bool playing, string? followed, bool showing, bool expected) =>
        Assert.Equal(expected, MediaModule.ShouldFollow(candidate, playing, followed, showing));

    [Fact]
    public void Players_with_unspecified_seek_bounds_use_the_track_timeline()
    {
        Assert.Equal(TimeSpan.FromSeconds(120), MediaModule.SeekPosition(.5, TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(220), TimeSpan.Zero, TimeSpan.Zero));
    }

    [Fact]
    public void Invalid_timeline_or_progress_cannot_seek()
    {
        Assert.Null(MediaModule.SeekPosition(double.NaN, TimeSpan.Zero, TimeSpan.FromMinutes(3), TimeSpan.Zero, TimeSpan.FromMinutes(3)));
        Assert.Null(MediaModule.SeekPosition(double.PositiveInfinity, TimeSpan.Zero, TimeSpan.FromMinutes(3), TimeSpan.Zero, TimeSpan.FromMinutes(3)));
        Assert.Null(MediaModule.SeekPosition(0.5, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero));
        Assert.Null(MediaModule.SeekPosition(0.5, TimeSpan.Zero, TimeSpan.FromMinutes(3), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public async Task Playback_progress_does_not_send_seek_commands_and_unsupported_players_cannot_seek()
    {
        var positions = new List<double>();
        var vm = new MediaViewModel(() => Task.CompletedTask, () => Task.CompletedTask, () => Task.CompletedTask,
            progress => { positions.Add(progress); return Task.CompletedTask; });

        vm.Progress = 0.25;
        Assert.Empty(positions);
        Assert.False(vm.SeekCommand.CanExecute(0.5));
        vm.CanSeek = true;
        Assert.True(vm.SeekCommand.CanExecute(0.5));
        await vm.SeekCommand.ExecuteAsync(0.5);
        Assert.Equal([0.5], positions);
    }

    [Fact]
    public void Slider_commits_drag_or_click_once_and_restores_canceled_drags()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                var positions = new List<double>();
                MediaViewModel vm = null!;
                vm = new MediaViewModel(() => Task.CompletedTask, () => Task.CompletedTask, () => Task.CompletedTask,
                    progress => { positions.Add(progress); vm.Progress = progress; return Task.CompletedTask; })
                { HasSession = true, HasTimeline = true, CanSeek = true, Progress = 0.25 };
                var resources = new ResourceDictionary();
                resources.MergedDictionaries.Add(new ResourceDictionary
                { Source = new Uri("/Atoll;component/Theme/Theme.xaml", UriKind.Relative) });
                Application.Current.Resources = resources;
                resources.MergedDictionaries.Add(new MediaTemplates());
                var panel = (FrameworkElement)((DataTemplate)resources["MediaViewModel.Panel"]).LoadContent();
                panel.Resources = resources;
                panel.DataContext = vm;
                panel.Measure(new Size(600, 300));
                panel.Arrange(new Rect(0, 0, 600, 300));
                panel.UpdateLayout();
                var slider = FindSlider(panel)!;
                Assert.NotNull(slider);
                slider.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
                Assert.Equal(0.25, slider.Value);

                slider.RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent });
                Assert.True(vm.IsScrubbing);
                slider.SetCurrentValue(Slider.ValueProperty, 0.75);
                Assert.Empty(positions);
                slider.RaiseEvent(new DragCompletedEventArgs(0, 0, false) { RoutedEvent = Thumb.DragCompletedEvent });
                Assert.False(vm.IsScrubbing);
                Assert.Equal([0.75], positions);

                slider.RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent });
                slider.SetCurrentValue(Slider.ValueProperty, 0.9);
                slider.RaiseEvent(new DragCompletedEventArgs(0, 0, true) { RoutedEvent = Thumb.DragCompletedEvent });
                Assert.Equal(0.75, slider.Value);
                Assert.Single(positions);

                slider.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
                { RoutedEvent = UIElement.PreviewMouseDownEvent });
                Assert.True(vm.IsScrubbing);
                slider.SetCurrentValue(Slider.ValueProperty, 0.5);
                slider.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
                { RoutedEvent = UIElement.PreviewMouseUpEvent });
                Assert.Equal([0.75, 0.5], positions);
                vm.Progress = 0.6;
                slider.GetBindingExpression(Slider.ValueProperty)!.UpdateTarget();
                Assert.Equal(0.6, slider.Value);
                Assert.Equal(2, positions.Count);
                GroupedPanelsTests.VerifyTemplates();
                SettingsUiTests.VerifySettings();
                CalendarUiTests.Verify();
            }
            catch (Exception ex) { failure = ex; }
            finally { Application.Current?.Shutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true; // a hung test fails at its timeout instead of keeping the test host alive
        thread.Start();
        thread.Join();
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static Slider? FindSlider(DependencyObject parent)
    {
        if (parent is Slider slider) return slider;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            if (FindSlider(VisualTreeHelper.GetChild(parent, i)) is { } found) return found;
        return null;
    }
}
