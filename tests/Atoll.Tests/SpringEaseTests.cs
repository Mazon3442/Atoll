using Atoll.Settings;
using Atoll.Shell;

namespace Atoll.Tests;

public class SpringEaseTests
{
    [Fact]
    public void Starts_at_zero_ends_at_one_with_small_overshoot()
    {
        var ease = new SpringEase();
        Assert.Equal(0, ease.Ease(0), 3);
        Assert.Equal(1, ease.Ease(1), 3);

        var samples = Enumerable.Range(0, 101).Select(i => ease.Ease(i / 100.0)).ToList();
        Assert.InRange(samples.Max(), 1.0, 1.1);   // bounces past the target a little
        Assert.InRange(samples[^2], 0.99, 1.01);  // settled before the end, so no jump at t = 1
    }

    [Fact]
    public void Smooth_spring_settles_without_overshoot()
    {
        var ease = new SpringEase(1);
        var samples = Enumerable.Range(0, 101).Select(i => ease.Ease(i / 100.0)).ToList();
        Assert.Equal(0, samples[0], 3);
        Assert.True(samples.Max() <= 1);
        Assert.InRange(samples[^2], 0.99, 1);
    }

    [Fact]
    public void Snappy_spring_overshoots_less_and_settles_sooner()
    {
        var ease = new SpringEase(0.82, 16);
        Assert.Equal(0, ease.Ease(0), 3);
        Assert.Equal(1, ease.Ease(1), 3);

        var samples = Enumerable.Range(0, 101).Select(i => ease.Ease(i / 100.0)).ToList();
        Assert.InRange(samples.Max(), 1.0, 1.02);  // tighter bounce than Bouncy's
        Assert.InRange(samples[^2], 0.99, 1.01);   // settled before t = 1, with no jump at the end
        Assert.True(samples[50] > 0.99);           // effectively there halfway through, so it feels quick
    }

    [Fact]
    public void Motion_presets_keep_the_shipped_numbers_for_bouncy_and_smooth()
    {
        Assert.Equal((320, 140), (MotionPreset.Bouncy.MorphMs, MotionPreset.Bouncy.FadeMs));
        Assert.Equal((320, 140), (MotionPreset.Smooth.MorphMs, MotionPreset.Smooth.FadeMs));
        Assert.Equal((220, 110), (MotionPreset.Snappy.MorphMs, MotionPreset.Snappy.FadeMs));
        Assert.Same(MotionPreset.Bouncy, MotionPreset.For(MotionStyle.Bouncy));
        Assert.Same(MotionPreset.Smooth, MotionPreset.For(MotionStyle.Smooth));
        Assert.Same(MotionPreset.Snappy, MotionPreset.For(MotionStyle.Snappy));
    }

    [Fact]
    public void Notch_rounds_only_the_bottom_and_drops_the_top_rim()
    {
        Exception? error = null;
        var thread = new Thread(() => { try { CheckNotch(); } catch (Exception e) { error = e; } });
        thread.SetApartmentState(ApartmentState.STA); // WPF elements need an STA thread
        thread.Start();
        thread.Join();
        if (error is not null) throw error;
    }

    private static void CheckNotch()
    {
        var border = new PillBorder { Radius = 12 };
        Assert.Equal(new System.Windows.CornerRadius(12), border.CornerRadius);
        border.Notch = true;
        Assert.Equal(new System.Windows.CornerRadius(0, 0, 12, 12), border.CornerRadius);
        Assert.Equal(new System.Windows.Thickness(0), border.BorderThickness); // the rim is drawn along the shoulders instead
        border.Notch = false;
        Assert.Equal(new System.Windows.Thickness(1), border.BorderThickness);
    }

    [Fact]
    public void Notch_shoulders_flare_to_the_screen_edge_and_the_body_sits_inside_them()
    {
        Exception? error = null;
        var thread = new Thread(() => { try { CheckShoulders(); } catch (Exception e) { error = e; } });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null) throw error;
    }

    private static void CheckShoulders()
    {
        var shape = PillBorder.NotchGeometry(400, 200, 20, outline: false);
        var s = PillBorder.MaxShoulder;
        Assert.True(shape.FillContains(new System.Windows.Point(200, 1)));        // the top edge is solid
        Assert.False(shape.FillContains(new System.Windows.Point(1, 100)));       // below the shoulder the body is inset
        Assert.False(shape.FillContains(new System.Windows.Point(2, 1)));         // the flare tapers to a point at the corner
        Assert.True(shape.FillContains(new System.Windows.Point(s - 1, 2)));      // and fills in toward the body
        Assert.True(shape.FillContains(new System.Windows.Point(s + 2, 100)));
        Assert.True(shape.FillContains(new System.Windows.Point(400 - s - 2, 100)));
        Assert.False(shape.FillContains(new System.Windows.Point(400 - 1, 100)));
        Assert.True(shape.Bounds.Width <= 400.001);
    }
}
