using System;
using System.Threading;
using System.Threading.Tasks;
using Screenbox.Core.Models;
using Windows.Storage;

namespace Screenbox.Core.Services;

/// <summary>
/// Finds, downloads and launches app updates published as GitHub releases.
/// </summary>
public interface IAppUpdateService
{
    /// <summary>Gets the version of the installed package.</summary>
    Version CurrentVersion { get; }

    /// <summary>
    /// Gets whether this install came from the GitHub releases. Development builds have a different package
    /// identity, and installing a release over them would add a second app instead of updating this one.
    /// </summary>
    bool IsReleaseBuild { get; }

    /// <summary>
    /// Looks up the latest release.
    /// </summary>
    /// <returns>The release when it is newer than <see cref="CurrentVersion"/>, otherwise <see langword="null"/>.</returns>
    Task<AppUpdateInfo?> CheckForUpdateAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Downloads the release package to the temporary folder.
    /// </summary>
    /// <param name="update">The release to download.</param>
    /// <param name="progress">Receives the downloaded fraction between 0 and 1.</param>
    /// <param name="cancellationToken">Cancels the download.</param>
    Task<StorageFile> DownloadAsync(AppUpdateInfo update, IProgress<double>? progress, CancellationToken cancellationToken = default);

    /// <summary>
    /// Opens the package with App Installer, which closes the app and installs the update.
    /// </summary>
    Task<bool> LaunchInstallerAsync(StorageFile package);
}
