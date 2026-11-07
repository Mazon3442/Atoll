using System.IO.Compression;
using Atoll.Modules.Notifications;

namespace Atoll.Tests;

public class NotificationRegistrationTests
{
    [Fact]
    public void The_bundled_package_version_is_read_from_its_manifest()
    {
        var directory = Path.Combine(Path.GetTempPath(), "atoll-identity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(directory, "out"));
        try
        {
            using (var package = ZipFile.Open(Path.Combine(directory, "out", "Atoll.Identity.msix"), ZipArchiveMode.Create))
            using (var writer = new StreamWriter(package.CreateEntry("AppxManifest.xml").Open()))
                writer.Write("""
                    <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10">
                      <Identity Name="Atoll" Publisher="CN=Atoll Dev" Version="1.0.3.0" ProcessorArchitecture="neutral" />
                    </Package>
                    """);
            Assert.Equal(new Version(1, 0, 3, 0), NotificationSetup.BundledVersion(directory));
            Assert.Null(NotificationSetup.BundledVersion(Path.Combine(directory, "missing")));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Theory]
    // The v0.4.0 registration (old placeholder logo) is replaced by a release's newer package.
    [InlineData("0.4.0.0", "1.0.3.0", true)]
    [InlineData("1.0.3.0", "1.0.3.0", false)]
    // A newer registration (or a local build stamped with the repository default) is never "downgraded".
    [InlineData("1.0.3.0", "0.4.0.0", false)]
    [InlineData(null, "1.0.3.0", false)]
    [InlineData("0.4.0.0", null, false)]
    public void An_older_registration_is_offered_an_update(string? registered, string? bundled, bool expected) =>
        Assert.Equal(expected, NotificationSetup.IsOutdated(registered is null ? null : Version.Parse(registered), bundled is null ? null : Version.Parse(bundled)));
}
