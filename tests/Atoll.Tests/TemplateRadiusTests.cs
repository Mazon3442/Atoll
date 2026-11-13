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
                // True pills and ladder radii go through shell:Round, which clamps to half the shorter side so no corner can turn into an oval.
                var allowed = Regex.IsMatch(value, @"^\{shell:Round(?:\s+(?:14|22))?\}$");
                Assert.True(allowed, $"{Path.GetRelativePath(modules, file)}:{((IXmlLineInfo)radius).LineNumber}: CornerRadius '{value}' must be {{shell:Round}} (a pill), {{shell:Round 14}} or {{shell:Round 22}}.");
            }
        }
    }

    private static string SourceFile([CallerFilePath] string path = "") => path;
}
