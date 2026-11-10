using System.Windows.Media;
using Atoll.Shell;
using Atoll.Theme;

namespace Atoll.Tests;

public sealed class GlassTests
{
    [Fact]
    public void Glass_tint_keeps_the_blur_visible()
    {
        var background = Color.FromRgb(0x1C, 0x1C, 0x1E);
        var tint = ThemeService.GlassTintFor(background, 0.8);

        // A wash, not a fill: at the default opacity the island must still show what is behind it.
        Assert.InRange(tint.A / 255.0, 0.35, 0.55);
        Assert.Equal(background.R, tint.R);
        Assert.Equal(background.G, tint.G);
        Assert.Equal(background.B, tint.B);

        // Even at full opacity the frost has to survive.
        Assert.InRange(ThemeService.GlassTintFor(background, 1).A / 255.0, 0.55, 0.65);
        Assert.True(ThemeService.GlassTintFor(background, 0.3).A < ThemeService.GlassTintFor(background, 1).A);
    }

    [Fact]
    public void Blur_behind_is_not_requested_where_windows_ignores_it()
    {
        // Windows 11 answers success and draws nothing, so the island must not trust that call there.
        if (Environment.OSVersion.Version.Build < 22000) return;
        Assert.False(SystemBackdrop.Supported);
    }
}
