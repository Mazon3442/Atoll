using Atoll.Desktop;
using Atoll.Modules.Media;
using Atoll.Settings;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Atoll.Desktop.Tests;

public sealed class MediaTests
{
    [Fact]
    public void Artwork_chooses_cover_color_and_skips_transparent_pixels()
    {
        byte[] cover = [0, 0, 220, 255, 0, 0, 220, 255, 255, 0, 0, 0];
        Assert.Equal("#DC0000", ArtworkPalette.FromPixels(cover, 3, 1, 12));
        Assert.Null(ArtworkPalette.FromPixels([0, 0, 0, 0], 1, 1, 4));
        Assert.Null(ArtworkPalette.FromPixels([], 2, 2, 8));
        Assert.NotEqual("#000000", ArtworkPalette.ForBackground("#000000", "#000000", "#00FF00"));
        Assert.NotEqual("#FFFFFF", ArtworkPalette.ForBackground("#FFFFFF", "#FFFFFF", "#00FF00"));
    }

    [Fact]
    public void Selected_source_survives_refresh_and_falls_back_only_when_it_closes()
    {
        var vm = NewModel([]);
        vm.SetSources([new("sidra", "Sidra"), new("firefox", "Firefox"), new("discord", "Discord (audio)")]);
        vm.SelectedSource = vm.Sources.Single(s => s.Id == "sidra");
        var changes = new List<string>(); vm.SourceChanged += changes.Add;
        vm.SetSources([new("firefox", "Firefox"), new("sidra", "Sidra")]);
        Assert.Equal("sidra", vm.SelectedSource!.Id); Assert.Empty(changes);
        vm.SetSources([new("firefox", "Firefox")]);
        Assert.Equal("auto", vm.SelectedSource!.Id); Assert.Equal(["auto"], changes);
    }

    [AvaloniaFact]
    public void Album_cover_colors_both_media_views_and_theme_fallback()
    {
        using var cover = new Avalonia.Media.Imaging.WriteableBitmap(new PixelSize(2, 2), new Vector(96, 96), Avalonia.Platform.PixelFormat.Bgra8888, Avalonia.Platform.AlphaFormat.Premul);
        using (var pixels = cover.Lock()) System.Runtime.InteropServices.Marshal.Copy(new byte[] { 0, 0, 220, 255, 0, 0, 220, 255, 0, 0, 220, 255, 0, 0, 220, 255 }, 0, pixels.Address, 16);
        var vm = NewModel([]); vm.Art = cover; Assert.Equal("#DC0000", vm.Visualizer.Color);
        vm.SetSources([new("sidra", "Sidra"), new("firefox", "Firefox"), new("discord", "Discord (audio)")]);
        var panel = Views.Stack(Views.Compact(vm), Views.For(vm)); var window = new Window { Width = 520, Height = 650, Content = panel }; window.Show(); Dispatcher.UIThread.RunJobs();
        Assert.Equal(10, window.GetVisualDescendants().OfType<Border>().Count(b => b.Width == 3 && b.Background is Avalonia.Media.ISolidColorBrush brush && brush.Color == Avalonia.Media.Color.Parse("#DC0000")));
        using var frame = window.CaptureRenderedFrame(); Assert.NotNull(frame);
        var output = Environment.GetEnvironmentVariable("ATOLL_TEST_SCREENSHOTS") ?? Path.Combine(Path.GetTempPath(), "atoll-desktop-review"); Directory.CreateDirectory(output); frame.Save(Path.Combine(output, "media-cover-color.png"));
        vm.Art = null; vm.ApplyTheme(new ThemeSettings { Background = "#000000", Accent = "#00FF00" }); Assert.Equal("#00FF00", vm.Visualizer.Color);
        window.Close();
    }

    [AvaloniaFact]
    public void Seek_commits_pointer_and_keyboard_input_and_cancels_on_track_change()
    {
        var seeks = new List<double>(); var vm = NewModel(seeks);
        var pane = Views.For(vm); var window = new Window { Width = 520, Height = 650, Content = pane };
        window.Show(); Dispatcher.UIThread.RunJobs();
        var slider = window.GetVisualDescendants().OfType<MediaSeekSlider>().Single();
        var point = slider.TranslatePoint(new Point(slider.Bounds.Width * .7, slider.Bounds.Height / 2), window)!.Value;
        window.MouseDown(point, MouseButton.Left); Assert.True(vm.IsScrubbing);
        window.MouseUp(point, MouseButton.Left); Dispatcher.UIThread.RunJobs();
        Assert.Single(seeks); Assert.InRange(seeks[0], .6, .8); Assert.False(vm.IsScrubbing);
        slider.Focus(); window.KeyPressQwerty(PhysicalKey.ArrowRight, RawInputModifiers.None); window.KeyReleaseQwerty(PhysicalKey.ArrowRight, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs(); Assert.Equal(2, seeks.Count);
        window.MouseDown(point, MouseButton.Left); vm.Title = "Another track";
        window.MouseUp(point, MouseButton.Left); Dispatcher.UIThread.RunJobs(); Assert.Equal(2, seeks.Count);
        window.MouseDown(point, MouseButton.Left); vm.InvalidatePlayback();
        window.MouseUp(point, MouseButton.Left); Dispatcher.UIThread.RunJobs(); Assert.Equal(2, seeks.Count);
        window.Close();
    }

    private static MediaViewModel NewModel(List<double> seeks)
    {
        MediaViewModel vm = null!;
        vm = new MediaViewModel(() => Task.CompletedTask, () => Task.CompletedTask, () => Task.CompletedTask,
            value => { seeks.Add(value); vm.Progress = value; return Task.CompletedTask; })
        { CanControl = true, CanSeek = true, HasTimeline = true, HasSession = true, Title = "Test track", Progress = .25 };
        return vm;
    }
}
