using System;
using System.Collections.Generic;
using System.Globalization;
using Screenbox.Core;
using Screenbox.Core.Downloads.Contracts;
using Screenbox.Core.ViewModels;
using Screenbox.Strings;
using Windows.UI.Xaml;

namespace Screenbox.Helpers;

/// <summary>
/// Localized text for the Downloads page and the update UI. Lives in the view layer because view models must not
/// read string resources.
/// </summary>
public static class DownloadLabelHelper
{
    private const string Separator = " · ";
    private static readonly string[] SizeUnits = ["B", "KB", "MB", "GB", "TB"];

    /// <summary>
    /// Gets the title to show for a job, falling back to its link while the title is not known yet.
    /// </summary>
    public static string GetTitle(string? title, string sourceUrl) => string.IsNullOrWhiteSpace(title) ? sourceUrl : title;

    /// <summary>
    /// Joins channel and playlist into one secondary line.
    /// </summary>
    public static string GetSubtitle(string? channel, string? collection, TimeSpan? duration)
    {
        List<string> parts = new(3);
        if (!string.IsNullOrWhiteSpace(channel)) parts.Add(channel);
        if (!string.IsNullOrWhiteSpace(collection)) parts.Add(collection);
        if (duration is TimeSpan length && length > TimeSpan.Zero) parts.Add(Humanizer.ToDuration(length));
        return string.Join(Separator, parts);
    }

    /// <summary>
    /// Builds the status line: state, then size, speed and time left while downloading, or the error when failed.
    /// </summary>
    public static string GetStatus(DownloadState state, long? downloaded, long? total, double? speed, TimeSpan? eta, string? error)
    {
        string label = GetStateLabel(state);
        if (state == DownloadState.Failed && !string.IsNullOrWhiteSpace(error))
        {
            return label + Separator + error;
        }

        if (state != DownloadState.Downloading) return label;

        List<string> parts = new(4) { label };
        if (downloaded is long done && total is long all && all > 0)
        {
            parts.Add(Resources.DownloadSizeProgress(FormatSize(done), FormatSize(all)));
        }

        if (speed is double bytesPerSecond && bytesPerSecond > 0)
        {
            parts.Add(Resources.DownloadSpeed(FormatSize((long)bytesPerSecond)));
        }

        if (eta is TimeSpan left && left > TimeSpan.Zero)
        {
            parts.Add(Resources.DownloadTimeLeft(Humanizer.ToDuration(left)));
        }

        return string.Join(Separator, parts);
    }

    public static string GetStateLabel(DownloadState state) => state switch
    {
        DownloadState.Resolving => Resources.DownloadStateResolving,
        DownloadState.Queued => Resources.DownloadStateQueued,
        DownloadState.Downloading => Resources.DownloadStateDownloading,
        DownloadState.Processing => Resources.DownloadStateProcessing,
        DownloadState.Completed => Resources.DownloadStateCompleted,
        DownloadState.Failed => Resources.DownloadStateFailed,
        DownloadState.Canceled => Resources.DownloadStateCanceled,
        _ => string.Empty,
    };

    public static string GetQualityLabel(DownloadQuality quality) => quality switch
    {
        DownloadQuality.Best => Resources.DownloadQualityBest,
        DownloadQuality.Max1080p => Resources.DownloadQuality1080p,
        DownloadQuality.Max720p => Resources.DownloadQuality720p,
        DownloadQuality.Max480p => Resources.DownloadQuality480p,
        DownloadQuality.AudioOnly => Resources.DownloadQualityAudioOnly,
        _ => string.Empty,
    };

    /// <summary>
    /// Shows the progress bar for anything that is not finished yet.
    /// </summary>
    public static Visibility ProgressVisibility(DownloadState state) => state is DownloadState.Completed ? Visibility.Collapsed : Visibility.Visible;

    public static string GetEngineText(string? version, bool isUpdating) =>
        isUpdating ? Resources.DownloadEngineUpdating : string.IsNullOrEmpty(version) ? string.Empty : Resources.DownloadEngineVersion(version);

    public static bool Not(bool value) => !value;

    public static bool HasText(string? value) => !string.IsNullOrWhiteSpace(value);

    // x:Bind functions that return bool for a Visibility target generate code that does not compile,
    // so the functions used for Visibility return it directly.
    public static Visibility CollapsedIf(bool value) => value ? Visibility.Collapsed : Visibility.Visible;

    public static Visibility VisibleIfText(string? value) => string.IsNullOrWhiteSpace(value) ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>
    /// Gets the line under the "Check for updates" button in Settings.
    /// </summary>
    public static string GetUpdateStatus(AppUpdateStatus status, string availableVersion) => status switch
    {
        AppUpdateStatus.Checking => Resources.UpdateStatusChecking,
        AppUpdateStatus.UpToDate => Resources.UpdateStatusUpToDate,
        AppUpdateStatus.Available => Resources.UpdateAvailableMessage(availableVersion),
        AppUpdateStatus.Downloading => Resources.UpdateStatusDownloading,
        AppUpdateStatus.Installing => Resources.UpdateStatusInstalling,
        AppUpdateStatus.Failed => Resources.UpdateStatusFailed,
        _ => string.Empty,
    };

    public static string GetUpdateBannerMessage(AppUpdateStatus status, string availableVersion) => status switch
    {
        AppUpdateStatus.Downloading => Resources.UpdateStatusDownloading,
        AppUpdateStatus.Installing => Resources.UpdateStatusInstalling,
        AppUpdateStatus.Failed => Resources.UpdateStatusFailed,
        _ => Resources.UpdateAvailableMessage(availableVersion),
    };

    public static string GetCurrentVersion(string version) => Resources.CurrentVersion(version);

    /// <summary>
    /// Formats a byte count with one decimal for MB and above, using the current culture's decimal separator.
    /// </summary>
    public static string FormatSize(long bytes)
    {
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < SizeUnits.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        string format = unit >= 2 ? "0.0" : "0";
        return value.ToString(format, CultureInfo.CurrentCulture) + " " + SizeUnits[unit];
    }
}
