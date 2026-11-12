using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using Atoll.Settings;

namespace Atoll.Modules.Media;

/// <summary>
/// The Bongo Cat joke: a vector cat whose paws drum while <see cref="Playing"/> is true and freeze where they are
/// when it is not. Turning off animations in Windows leaves the cat at rest.
/// </summary>
public partial class BongoCatView : UserControl
{
    public static readonly DependencyProperty VariantProperty = DependencyProperty.Register(
        nameof(Variant), typeof(BongoCatStyle), typeof(BongoCatView), new PropertyMetadata(BongoCatStyle.Classic, (d, _) => ((BongoCatView)d).Refresh()));

    public static readonly DependencyProperty PlayingProperty = DependencyProperty.Register(
        nameof(Playing), typeof(bool), typeof(BongoCatView), new PropertyMetadata(false, (d, _) => ((BongoCatView)d).Refresh()));

    public BongoCatStyle Variant { get => (BongoCatStyle)GetValue(VariantProperty); set => SetValue(VariantProperty, value); }
    public bool Playing { get => (bool)GetValue(PlayingProperty); set => SetValue(PlayingProperty, value); }

    private Storyboard? _drum;
    private BongoCatStyle _built;

    public BongoCatView()
    {
        InitializeComponent();
        Refresh();
        Loaded += (_, _) => Refresh();
        Unloaded += (_, _) => Stop();
        IsVisibleChanged += (_, _) => Refresh();
    }

    private void Refresh()
    {
        var excited = Variant == BongoCatStyle.Excited;
        var fill = Frozen(excited ? Color.FromRgb(0x3D, 0xDC, 0xD0) : Colors.White);
        var line = Frozen(excited ? Color.FromRgb(0x0F, 0x7C, 0x76) : Color.FromRgb(0x2A, 0x2A, 0x2E));
        var paw = Frozen(excited ? Color.FromRgb(0x7B, 0xEA, 0xE0) : Color.FromRgb(0xF6, 0xD3, 0xDA));
        foreach (var shape in new Shape[] { Ears, Skull }) { shape.Fill = fill; shape.Stroke = line; }
        foreach (var shape in new Shape[] { PawL, PawR }) { shape.Fill = paw; shape.Stroke = line; }
        Mouth.Stroke = line;
        EyeL.Fill = EyeR.Fill = line;

        if (!(Playing && IsLoaded && IsVisible && SystemParameters.ClientAreaAnimation))
        {
            _drum?.Pause(this); // keeps the pose it is in; a cat that never played rests with paws up
            return;
        }
        if (_drum is null || _built != Variant) Start(excited);
        else _drum.Resume(this);
    }

    private void Start(bool excited)
    {
        Stop();
        _built = Variant;
        var beat = TimeSpan.FromMilliseconds(excited ? 280 : 460);
        var drum = new Storyboard();
        drum.Children.Add(Beat(nameof(PawLShift), TimeSpan.Zero, beat, 24));
        drum.Children.Add(Beat(nameof(PawRShift), TimeSpan.FromTicks(beat.Ticks / 2), beat, 24));
        drum.Children.Add(Beat(nameof(HeadShift), TimeSpan.Zero, TimeSpan.FromTicks(beat.Ticks / 2), excited ? 5 : 3));
        drum.Begin(this, true);
        _drum = drum;
    }

    private void Stop()
    {
        if (_drum is null) return;
        _drum.Remove(this);
        _drum = null;
    }

    /// <summary>Up, slam, up: the target drops in the first fifth of the beat and lifts back over the rest.</summary>
    private static DoubleAnimationUsingKeyFrames Beat(string target, TimeSpan begin, TimeSpan length, double drop)
    {
        var anim = new DoubleAnimationUsingKeyFrames { Duration = length, RepeatBehavior = RepeatBehavior.Forever, BeginTime = begin };
        anim.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromPercent(0)));
        anim.KeyFrames.Add(new EasingDoubleKeyFrame(drop, KeyTime.FromPercent(0.2), new QuadraticEase { EasingMode = EasingMode.EaseIn }));
        anim.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromPercent(1), new QuadraticEase { EasingMode = EasingMode.EaseOut }));
        Storyboard.SetTargetName(anim, target);
        Storyboard.SetTargetProperty(anim, new PropertyPath(TranslateTransform.YProperty));
        return anim;
    }

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
