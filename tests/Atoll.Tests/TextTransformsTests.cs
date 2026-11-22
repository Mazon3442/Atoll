using Atoll.Modules.Text;

namespace Atoll.Tests;

public class TextTransformsTests
{
    [Theory]
    [InlineData("ghbdtn", "привет")]
    [InlineData("Ghbdtn? rfr ltkf?", "Привет, как дела,")]
    [InlineData("привет", "ghbdtn")]
    [InlineData("Руддщ цщкдв", "Hello world")]
    [InlineData("", "")]
    public void The_wrong_layout_is_retyped_in_the_other_script(string typed, string expected) =>
        Assert.Equal(expected, TextTransforms.FixLayout(typed));

    [Theory]
    [InlineData("fooBar baz-qux", "foo_bar_baz_qux")]
    [InlineData("Hello World 2", "hello_world_2")]
    [InlineData("HTTPServer", "http_server")]
    public void Snake_case_splits_on_humps_and_punctuation(string text, string expected) =>
        Assert.Equal(expected, TextTransforms.Snake(text));

    [Fact]
    public void Camel_kebab_title_and_sentence_cases()
    {
        Assert.Equal("fooBarBaz", TextTransforms.Camel("foo bar_baz"));
        Assert.Equal("foo-bar-baz", TextTransforms.Kebab("FooBar baz"));
        Assert.Equal("Hello World", TextTransforms.Title("hELLO wORLD"));
        Assert.Equal("Hello world. This is it! Ok?", TextTransforms.Sentence("hELLO world. this IS it! ok?"));
        Assert.Equal("", TextTransforms.Camel("  "));
    }

    [Theory]
    [InlineData("Привет, мир", "Privet, mir")]
    [InlineData("Щука", "Shchuka")]
    [InlineData("privet", "привет")]
    [InlineData("Shchuka", "Щука")]
    public void Transliteration_goes_to_whichever_script_the_text_is_not(string text, string expected) =>
        Assert.Equal(expected, TextTransforms.Transliterate(text));

    [Fact]
    public void Json_is_pretty_printed_minified_or_refused_with_a_reason()
    {
        var pretty = TextTransforms.JsonPretty("{\"a\":[1,2],\"b\":\"é\"}");
        Assert.True(pretty.Ok);
        Assert.Contains("\n", pretty.Text);
        Assert.Contains("é", pretty.Text); // not escaped

        Assert.Equal("{\"a\":[1,2]}", TextTransforms.JsonMinify("{ \"a\" : [ 1, 2 ] }").Text);

        var bad = TextTransforms.JsonPretty("{nope");
        Assert.False(bad.Ok);
        Assert.StartsWith("Not valid JSON", bad.Error);
    }

    [Fact]
    public void Base64_round_trips_and_accepts_url_safe_unpadded_input()
    {
        var encoded = TextTransforms.Base64Encode("héllo wörld ✓");
        Assert.Equal("héllo wörld ✓", TextTransforms.Base64Decode(encoded).Text);
        Assert.Equal("hi?>", TextTransforms.Base64Decode("aGk_Pg").Text);
        Assert.False(TextTransforms.Base64Decode("***").Ok);
    }

    [Fact]
    public void Url_text_round_trips()
    {
        Assert.Equal("a%20b%26c", TextTransforms.UrlEncode("a b&c"));
        Assert.Equal("a b&c", TextTransforms.UrlDecode("a%20b%26c").Text);
        Assert.Equal("a b", TextTransforms.UrlDecode("a+b").Text);
    }

    [Fact]
    public void Clean_lines_trims_and_drops_blanks() =>
        Assert.Equal("a" + Environment.NewLine + "b", TextTransforms.CleanLines("  a \r\n\r\n   \n b  "));
}
