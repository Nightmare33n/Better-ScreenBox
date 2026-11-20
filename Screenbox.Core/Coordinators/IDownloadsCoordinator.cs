using System;
using System.Threading.Tasks;
using Screenbox.Core.Downloads.Contracts;

namespace Screenbox.Core.Coordinators;

/// <summary>
/// Keeps <see cref="Contexts.DownloadsContext"/> in sync with the download helper and forwards user actions to it.
/// </summary>
public interface IDownloadsCoordinator : IDisposable
{
    /// <summary>
    /// Starts mirroring the queue and resumes downloads left pending by a previous session. Safe to call repeatedly.
    /// </summary>
    Task StartAsync();

    /// <summary>Queues a video or playlist link.</summary>
    Task AddAsync(string url, DownloadQuality quality);

    Task CancelAsync(string jobId);

    Task RetryAsync(string jobId);

    /// <summary>Removes a job from the list. Downloaded files are kept.</summary>
    Task RemoveAsync(string jobId);

    /// <summary>Removes every completed, failed or canceled job from the list.</summary>
    Task ClearFinishedAsync();

    /// <summary>Asks the helper to update yt-dlp now.</summary>
    Task UpdateEngineAsync();
}
