using Atoll.Modules.Indicators;

namespace Atoll.Tests;

public class KeyboardLayoutLabelTests
{
    [Theory]
    [InlineData(0x0409, "EN", "English")]
    [InlineData(0x0419, "RU", "Russian")]
    [InlineData(0x0407, "DE", "German")]
    [InlineData(0xF0409, "EN", "English")] // a handle's high word is ignored
    public void Layouts_are_named_by_language(int id, string code, string name) =>
        Assert.Equal((code, name), KeyboardLayoutLabel.For(id));

    [Fact]
    public void An_unknown_layout_still_gets_a_label()
    {
        var (code, name) = KeyboardLayoutLabel.For(0x7FFF);
        Assert.False(string.IsNullOrEmpty(code));
        Assert.False(string.IsNullOrEmpty(name));
    }
}
