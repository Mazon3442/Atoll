using Atoll.Modules.Media;
using Atoll.Theme;
using NAudio.Wave;

namespace Atoll.Tests;

public class ThemeAndAudioTests
{
    [Fact]
    public void Loopback_float_formats_are_accepted_including_extensible()
    {
        // Windows' shared-mode mix format: 32-bit float wrapped in WAVE_FORMAT_EXTENSIBLE (e.g. 7.1 at 96 kHz).
        Assert.True(AudioLevels.IsFloat32(new WaveFormatExtensible(96000, 32, 8)));
        Assert.True(AudioLevels.IsFloat32(WaveFormat.CreateIeeeFloatWaveFormat(48000, 2)));
        Assert.False(AudioLevels.IsFloat32(new WaveFormat(44100, 16, 2)));
    }

    [Fact]
    public void Presets_have_unique_names_and_valid_colors()
    {
        var presets = ThemeService.Presets;
        Assert.Equal(presets.Count, presets.Select(p => p.Name).Distinct().Count());
        Assert.All(presets, p =>
        {
            Assert.True(ThemeService.TryParseColor(p.Background, out _), p.Name);
            Assert.True(ThemeService.TryParseColor(p.Foreground, out _), p.Name);
            Assert.True(ThemeService.TryParseColor(p.Accent, out _), p.Name);
        });
        Assert.Contains(presets, p => p.Glass);
    }
}
