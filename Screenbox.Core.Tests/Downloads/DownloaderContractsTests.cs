using System.Text.Json;
using Screenbox.Core.Downloads.Contracts;
using Screenbox.Core.ViewModels;

namespace Screenbox.Core.Tests.Downloads;

public sealed class DownloaderContractsTests
{
    [Test]
    public async Task Snapshot_RoundTrips_WithStringEnums()
    {
        DownloaderSnapshot original = new()
        {
            Heartbeat = new DateTimeOffset(2026, 11, 20, 8, 0, 0, TimeSpan.Zero),
            IsRunning = true,
            EngineVersion = "2026.10.26",
            Jobs =
            [
                new DownloadJob
                {
                    Id = "abc",
                    SourceUrl = "https://www.youtube.com/watch?v=jNQXAC9IVRw",
                    Title = "Me at the zoo",
                    Quality = DownloadQuality.Max720p,
                    State = DownloadState.Downloading,
                    Progress = 0.5,
                    IsResolved = true,
                },
            ],
        };

        string json = JsonSerializer.Serialize(original, DownloaderJsonContext.Default.DownloaderSnapshot);
        await Assert.That(json).Contains("\"state\":\"Downloading\"");
        await Assert.That(json).Contains("\"quality\":\"Max720p\"");
        await Assert.That(json).DoesNotContain("\"filePath\"");

        DownloaderSnapshot? copy = JsonSerializer.Deserialize(json, DownloaderJsonContext.Default.DownloaderSnapshot);
        await Assert.That(copy).IsNotNull();
        await Assert.That(copy!.Jobs.Count).IsEqualTo(1);
        await Assert.That(copy.Jobs[0].State).IsEqualTo(DownloadState.Downloading);
        await Assert.That(copy.Jobs[0].Progress).IsEqualTo(0.5);
        await Assert.That(copy.EngineVersion).IsEqualTo("2026.10.26");
    }

    [Test]
    public async Task Command_Deserializes_FromCamelCaseJson()
    {
        const string json = """{"kind":"Add","url":"https://youtu.be/jNQXAC9IVRw","quality":"AudioOnly"}""";

        DownloaderCommand? command = JsonSerializer.Deserialize(json, DownloaderJsonContext.Default.DownloaderCommand);

        await Assert.That(command).IsNotNull();
        await Assert.That(command!.Kind).IsEqualTo(DownloaderCommandKind.Add);
        await Assert.That(command.Quality).IsEqualTo(DownloadQuality.AudioOnly);
        await Assert.That(command.Url).IsEqualTo("https://youtu.be/jNQXAC9IVRw");
    }

    [Test]
    [Arguments("https://www.youtube.com/watch?v=jNQXAC9IVRw", true)]
    [Arguments("http://youtu.be/jNQXAC9IVRw", true)]
    [Arguments("  https://www.youtube.com/playlist?list=PL123  ", true)]
    [Arguments("youtube.com/watch?v=jNQXAC9IVRw", false)]
    [Arguments("ftp://example.com/video.mp4", false)]
    [Arguments("", false)]
    [Arguments(null, false)]
    public async Task IsWebLink_AcceptsOnlyHttpLinks(string? text, bool expected)
    {
        await Assert.That(DownloadsPageViewModel.IsWebLink(text)).IsEqualTo(expected);
    }
}
