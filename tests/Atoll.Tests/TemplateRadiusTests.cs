using System.Runtime.CompilerServices;
using System.Xml;
using System.Xml.Linq;
using System.Text.RegularExpressions;

namespace Atoll.Tests;

public class TemplateRadiusTests
{
    [Fact]
    public void Module_templates_use_the_radius_ladder()
    {
        var modules = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(SourceFile())!, "../../src/Atoll.App/Modules"));
        var files = Directory.GetFiles(modules, "*.xaml", SearchOption.AllDirectories);
        Assert.NotEmpty(files);

        foreach (var file in files)
        {
            var document = XDocument.Parse(File.ReadAllText(file), LoadOptions.SetLineInfo);
            var radii = document.Descendants().SelectMany(element => element.Attributes().Where(attribute =>
                attribute.Name.LocalName == "CornerRadius" ||
                element.Name.LocalName == "Setter" && (string?)element.Attribute("Property") == "CornerRadius" && attribute.Name.LocalName == "Value"));

            foreach (var radius in radii)
            {
                var value = radius.Value.Trim();
                var allowed = value is "14" or "22" ||
                    Regex.IsMatch(value, @"^\{Binding\s+Actual(?:Height|Width),\s*RelativeSource=\{RelativeSource\s+Self\},\s*Converter=\{x:Static\s+\w+:PillRadius\.Instance\}\}$") ||
                    Regex.IsMatch(value, @"^\{(?:StaticResource|DynamicResource)\s+Radius\.(?:Card|Control)\}$");
                Assert.True(allowed, $"{Path.GetRelativePath(modules, file)}:{((IXmlLineInfo)radius).LineNumber}: CornerRadius '{value}' must use 14, 22, the corresponding radius resource, or PillRadius.");
            }
        }
    }

    private static string SourceFile([CallerFilePath] string path = "") => path;
}
