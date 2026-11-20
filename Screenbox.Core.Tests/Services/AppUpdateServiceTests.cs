using System.Text.Json;
using Screenbox.Core.Models;
using Screenbox.Core.Services;

namespace Screenbox.Core.Tests.Services;

public sealed class AppUpdateServiceTests
{
    [Test]
    [Arguments("v1.2.3", "1.2.3.0")]
    [Arguments("1.2.3.4", "1.2.3.4")]
    [Arguments("V2.0", "2.0.0.0")]
    [Arguments("v1.4.0-beta", "1.4.0.0")]
    public async Task TryParseVersion_NormalizesToFourParts(string tag, string expected)
    {
        bool parsed = AppUpdateService.TryParseVersion(tag, out Version? version);

        await Assert.That(parsed).IsTrue();
        await Assert.That(version!.ToString()).IsEqualTo(expected);
    }

    [Test]
    [Arguments("latest")]
    [Arguments("")]
    public async Task TryParseVersion_RejectsNonVersionTags(string tag)
    {
        await Assert.That(AppUpdateService.TryParseVersion(tag, out _)).IsFalse();
    }

    [Test]
    public async Task ParseRelease_PrefersBundleOverSinglePackage()
    {
        const string json = """
            {
              "tag_name": "v1.1.0",
              "html_url": "https://github.com/owner/repo/releases/tag/v1.1.0",
              "body": "Notes",
              "draft": false,
              "prerelease": false,
              "assets": [
                { "name": "Screenbox_1.1.0.0_x64.msix", "size": 10, "browser_download_url": "https://example.com/a.msix" },
                { "name": "Screenbox_1.1.0.0_x64.msixbundle", "size": 20, "browser_download_url": "https://example.com/b.msixbundle" },
                { "name": "Screenbox.cer", "size": 1, "browser_download_url": "https://example.com/c.cer" }
              ]
            }
            """;

        using JsonDocument document = JsonDocument.Parse(json);
        AppUpdateInfo? release = AppUpdateService.ParseRelease(document.RootElement);

        await Assert.That(release).IsNotNull();
        await Assert.That(release!.Version).IsEqualTo(new Version(1, 1, 0, 0));
        await Assert.That(release.PackageFileName).IsEqualTo("Screenbox_1.1.0.0_x64.msixbundle");
        await Assert.That(release.PackageSize).IsEqualTo(20L);
        await Assert.That(release.PackageUri.AbsoluteUri).IsEqualTo("https://example.com/b.msixbundle");
    }

    [Test]
    public async Task ParseRelease_SkipsPrereleases()
    {
        const string json = """
            { "tag_name": "v9.0.0", "html_url": "https://github.com/o/r", "prerelease": true,
              "assets": [ { "name": "a.msixbundle", "size": 1, "browser_download_url": "https://example.com/a.msixbundle" } ] }
            """;

        using JsonDocument document = JsonDocument.Parse(json);
        await Assert.That(AppUpdateService.ParseRelease(document.RootElement)).IsNull();
    }

    [Test]
    public async Task ParseRelease_ReturnsNullWithoutPackage()
    {
        const string json = """
            { "tag_name": "v9.0.0", "html_url": "https://github.com/o/r",
              "assets": [ { "name": "notes.txt", "size": 1, "browser_download_url": "https://example.com/notes.txt" } ] }
            """;

        using JsonDocument document = JsonDocument.Parse(json);
        await Assert.That(AppUpdateService.ParseRelease(document.RootElement)).IsNull();
    }
}
