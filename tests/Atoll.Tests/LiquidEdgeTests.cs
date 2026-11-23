using Atoll.Modules.Liquid;

namespace Atoll.Tests;

public class LiquidEdgeTests
{
    [Fact]
    public void The_edge_meets_the_sides_cleanly()
    {
        Assert.Equal(0, LiquidEdge.Rise(10, 10, 210, 0.3, 3));   // at the left end
        Assert.Equal(0, LiquidEdge.Rise(210, 10, 210, 0.3, 3));  // at the right end
        Assert.Equal(0, LiquidEdge.Rise(-5, 10, 210, 0.3, 3));   // outside
        Assert.True(LiquidEdge.Rise(11, 10, 210, Math.PI / 2, 3) < 0.5); // barely rises next to a corner
    }

    [Fact]
    public void The_ripple_never_goes_negative_or_past_the_limit()
    {
        double[] loud = [1, 1, 1, 1, 1];
        for (var x = 0.0; x <= 300; x += 3)
            for (var phase = 0.0; phase < 7; phase += 0.7)
            {
                var rise = LiquidEdge.Rise(x, 0, 300, phase, 2, loud, boost: 50);
                Assert.InRange(rise, 0, LiquidEdge.MaxRise);
            }
    }

    [Fact]
    public void A_still_edge_with_no_amplitude_is_flat()
    {
        for (var x = 0.0; x <= 200; x += 5) Assert.Equal(0, LiquidEdge.Rise(x, 0, 200, 1.2, 0));
    }

    [Fact]
    public void Music_raises_the_edge_where_the_band_is_loud()
    {
        double[] bassOnLeft = [1, 0, 0, 0, 0];
        // Pick two points on a crest (the sine peaks at phase = pi/2 - x*2pi/wavelength).
        double CrestPhase(double x) => Math.PI / 2 - x / LiquidEdge.Wavelength * 2 * Math.PI;
        var left = LiquidEdge.Rise(60, 0, 400, CrestPhase(60), 1, bassOnLeft);
        var right = LiquidEdge.Rise(340, 0, 400, CrestPhase(340), 1, bassOnLeft);
        Assert.True(left > right, $"{left} should beat {right}");
    }

    [Fact]
    public void Advancing_the_phase_moves_the_ripple()
    {
        var a = LiquidEdge.Rise(100, 0, 300, 0, 3);
        var b = LiquidEdge.Rise(100, 0, 300, 2, 3);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Levels_interpolate_between_bands()
    {
        double[] bands = [0, 1];
        Assert.Equal(0, LiquidEdge.Level(bands, 0), 9);
        Assert.Equal(0.5, LiquidEdge.Level(bands, 0.5), 9);
        Assert.Equal(1, LiquidEdge.Level(bands, 1), 9);
        Assert.Equal(0, LiquidEdge.Level(null, 0.5));
        Assert.Equal(0.7, LiquidEdge.Level([0.7], 0.9), 9);
    }

    [Fact]
    public void Samples_run_across_the_edge_and_end_on_the_corner()
    {
        var points = LiquidEdge.Sample(20, 220, 4, 0, 3);
        Assert.Equal(20, points[0].X);
        Assert.Equal((220, 0), (points[^1].X, points[^1].Rise));
        Assert.True(points.Count >= 50);
        Assert.Empty(LiquidEdge.Sample(10, 10, 4, 0, 3));
    }
}

public class LiquidShapeTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void The_liquid_outline_stays_inside_the_pill_and_closes_for_every_shape(bool notch, bool outline)
    {
        var shape = Atoll.Shell.LiquidShape.Build(300, 40, 20, notch, outline, 1.0, 2, [0.5, 1, 0.2, 0.8, 0.4]);
        var bounds = shape.Bounds;
        Assert.InRange(bounds.Left, -0.01, 0.01);
        Assert.InRange(bounds.Right, 299.99, 300.01);
        Assert.InRange(bounds.Top, -0.01, 0.01);
        Assert.InRange(bounds.Bottom, 40 - Atoll.Modules.Liquid.LiquidEdge.MaxRise - 0.01, 40.01); // the ripple only eats into the bottom
        Assert.False(bounds.IsEmpty);
    }

    [Fact]
    public void With_no_ripple_the_liquid_fill_matches_the_plain_notch()
    {
        var liquid = Atoll.Shell.LiquidShape.Build(300, 40, 20, notch: true, outline: false, 0, 0, null);
        var plain = Atoll.Shell.PillBorder.NotchGeometry(300, 40, 20, outline: false);
        Assert.Equal(plain.Bounds.Width, liquid.Bounds.Width, 2);
        Assert.Equal(plain.Bounds.Height, liquid.Bounds.Height, 2);
    }
}

public class LiquidRenderTests
{
    [Fact]
    public void A_liquid_pill_and_notch_render_with_a_rippled_bottom_and_a_rim()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var panel = new System.Windows.Controls.StackPanel { Background = System.Windows.Media.Brushes.MidnightBlue, Width = 320, Margin = new System.Windows.Thickness(0) };
                foreach (var notch in new[] { false, true })
                {
                    var pill = new Atoll.Shell.PillBorder
                    {
                        Width = 260, Height = 44, Radius = 22, Notch = notch, Liquid = true, Margin = new System.Windows.Thickness(0, 12, 0, 12),
                        Background = System.Windows.Media.Brushes.Black, BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0x7A, 0x9C)),
                        HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                    };
                    panel.Children.Add(pill);
                    panel.Measure(new System.Windows.Size(320, 200));
                    panel.Arrange(new System.Windows.Rect(0, 0, 320, 140));
                    panel.UpdateLayout();
                    pill.SetLiquid(1.3, 2.2, [0.9, 0.5, 0.2, 0.7, 0.4]);
                    Assert.NotNull(pill.Clip);
                }
                panel.Measure(new System.Windows.Size(320, 140));
                panel.Arrange(new System.Windows.Rect(0, 0, 320, 140));
                panel.UpdateLayout();
                var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(320, 140, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                bitmap.Render(panel);
                var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                var dir = Path.Combine(Path.GetTempPath(), "atoll-tier-two-three-review");
                Directory.CreateDirectory(dir);
                using var output = File.Create(Path.Combine(dir, "liquid-edge.png"));
                encoder.Save(output);
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)));
        Assert.Null(failure);
    }
}
