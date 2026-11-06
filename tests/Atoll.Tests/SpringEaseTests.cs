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
}
