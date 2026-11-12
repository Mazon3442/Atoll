using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Atoll.Settings;

namespace Atoll.Modules.Media;

/// <summary>One decoded GIF frame, already composited onto the full canvas, and how long it shows.</summary>
internal readonly record struct GifFrame(BitmapSource Image, TimeSpan Delay);

/// <summary>
/// Plays the Bongo Cat GIFs. WPF decodes GIFs but does not animate them, so the frames are composited once and stepped
/// here: they advance while <see cref="Playing"/> is true and stop on the current frame when it is not. Turning off
/// animations in Windows leaves the cat on its first frame.
/// </summary>
public partial class BongoCatView : UserControl
{
    public static readonly DependencyProperty VariantProperty = DependencyProperty.Register(
        nameof(Variant), typeof(BongoCatStyle), typeof(BongoCatView), new PropertyMetadata(BongoCatStyle.Classic, (d, _) => ((BongoCatView)d).Refresh(true)));

    public static readonly DependencyProperty PlayingProperty = DependencyProperty.Register(
        nameof(Playing), typeof(bool), typeof(BongoCatView), new PropertyMetadata(false, (d, _) => ((BongoCatView)d).Refresh(false)));

    public BongoCatStyle Variant { get => (BongoCatStyle)GetValue(VariantProperty); set => SetValue(VariantProperty, value); }
    public bool Playing { get => (bool)GetValue(PlayingProperty); set => SetValue(PlayingProperty, value); }

    private static readonly Dictionary<BongoCatStyle, IReadOnlyList<GifFrame>> Cache = [];
    private readonly DispatcherTimer _timer = new();
    private IReadOnlyList<GifFrame> _frames = [];
    private int _index;

    public BongoCatView()
    {
        InitializeComponent();
        _timer.Tick += (_, _) => Step();
        Loaded += (_, _) => Refresh(false);
        Unloaded += (_, _) => _timer.Stop();
        IsVisibleChanged += (_, _) => Refresh(false);
        Refresh(true);
    }

    /// <summary>The frame on screen now; exposed so tests can see the cat stop and move.</summary>
    internal int FrameIndex => _index;
    internal bool IsRunning => _timer.IsEnabled;

    private void Refresh(bool variantChanged)
    {
        if (variantChanged)
        {
            _timer.Stop();
            _frames = Load(Variant);
            _index = 0;
            Show();
        }
        if (_frames.Count > 1 && Playing && IsLoaded && IsVisible && SystemParameters.ClientAreaAnimation)
        {
            if (!_timer.IsEnabled) { _timer.Interval = _frames[_index].Delay; _timer.Start(); }
        }
        else _timer.Stop(); // paused keeps the frame it is on
    }

    private void Step()
    {
        _index = (_index + 1) % _frames.Count;
        _timer.Interval = _frames[_index].Delay;
        Show();
    }

    private void Show() => Picture.Source = _frames.Count == 0 ? null : _frames[_index].Image;

    private static IReadOnlyList<GifFrame> Load(BongoCatStyle style)
    {
        if (style == BongoCatStyle.Off) return [];
        if (Cache.TryGetValue(style, out var cached)) return cached;
        var name = style == BongoCatStyle.Excited ? "excited" : "classic";
        IReadOnlyList<GifFrame> frames;
        try { frames = Decode(new Uri($"pack://application:,,,/Atoll;component/Assets/BongoCat/{name}.gif")); }
        catch (Exception ex) { Log.Info($"Bongo Cat {name} did not load: {ex.Message}"); frames = []; }
        return Cache[style] = frames;
    }

    /// <summary>Decodes every frame of a GIF onto the full canvas, honouring each frame's offset and disposal.</summary>
    internal static IReadOnlyList<GifFrame> Decode(Uri source)
    {
        var decoder = new GifBitmapDecoder(source, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var width = Query(decoder.Metadata, "/logscrdesc/Width", decoder.Frames[0].PixelWidth);
        var height = Query(decoder.Metadata, "/logscrdesc/Height", decoder.Frames[0].PixelHeight);
        var canvas = new Rect(0, 0, width, height);
        var result = new List<GifFrame>();
        BitmapSource? baseImage = null;
        foreach (var frame in decoder.Frames)
        {
            var left = Query(frame.Metadata, "/imgdesc/Left", 0);
            var top = Query(frame.Metadata, "/imgdesc/Top", 0);
            var area = new Rect(left, top, frame.PixelWidth, frame.PixelHeight);
            var delay = Query(frame.Metadata, "/grctlext/Delay", 10) * 10;
            var disposal = Query(frame.Metadata, "/grctlext/Disposal", 0);

            var shown = Compose(canvas, baseImage, frame, area, null);
            result.Add(new GifFrame(shown, TimeSpan.FromMilliseconds(Math.Max(delay, 20))));
            baseImage = disposal switch
            {
                2 => Compose(canvas, baseImage, null, area, area), // clear this frame's rectangle
                3 => baseImage,                                    // back to what was there before it
                _ => shown,
            };
        }
        return result;
    }

    private static BitmapSource Compose(Rect canvas, BitmapSource? under, BitmapSource? frame, Rect area, Rect? clear)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            if (under is not null)
            {
                if (clear is { } hole)
                    dc.PushClip(new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(canvas), new RectangleGeometry(hole)));
                dc.DrawImage(under, canvas);
                if (clear is not null) dc.Pop();
            }
            if (frame is not null) dc.DrawImage(frame, area);
        }
        var target = new RenderTargetBitmap((int)canvas.Width, (int)canvas.Height, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);
        target.Freeze();
        return target;
    }

    private static int Query(ImageMetadata? metadata, string query, int fallback) =>
        metadata is BitmapMetadata m && m.ContainsQuery(query) && m.GetQuery(query) is { } value ? Convert.ToInt32(value) : fallback;
}
