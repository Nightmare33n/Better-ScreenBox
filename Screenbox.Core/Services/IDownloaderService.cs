using System.Threading.Tasks;
using Screenbox.Core.Downloads.Contracts;

namespace Screenbox.Core.Services;

/// <summary>
/// Talks to the full-trust download helper through files in the package LocalState folder.
/// </summary>
public interface IDownloaderService
{
    /// <summary>
    /// Gets whether this device can run the helper. Full-trust processes only exist on desktop Windows.
    /// </summary>
    bool IsSupported { get; }

    /// <summary>
    /// Reads the latest queue state written by the helper.
    /// </summary>
    /// <returns>
    /// The snapshot, an empty snapshot when the helper never ran, or <see langword="null"/> when the file is
    /// being replaced right now and should be read again on the next tick.
    /// </returns>
    Task<DownloaderSnapshot?> ReadSnapshotAsync();

    /// <summary>
    /// Queues a command for the helper. The helper picks it up within a second once it runs.
    /// </summary>
    Task SendCommandAsync(DownloaderCommand command);

    /// <summary>
    /// Starts the helper. Starting it while it already runs is harmless: the second instance exits at once.
    /// </summary>
    /// <returns><see langword="true"/> when the launch request was accepted.</returns>
    Task<bool> LaunchHelperAsync();
}
