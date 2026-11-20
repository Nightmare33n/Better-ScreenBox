using System;
using CommunityToolkit.Mvvm.ComponentModel;
using Screenbox.Core.Downloads.Contracts;

namespace Screenbox.Core.ViewModels;

/// <summary>
/// Observable view of one job in the download queue. Updated in place from each helper snapshot so list
/// containers are reused instead of rebuilt.
/// </summary>
public sealed partial class DownloadItemViewModel : ObservableObject
{
    public string Id { get; }

    [ObservableProperty]
    public partial string SourceUrl { get; set; }

    [ObservableProperty]
    public partial string? Title { get; set; }

    [ObservableProperty]
    public partial string? Channel { get; set; }

    [ObservableProperty]
    public partial TimeSpan? Duration { get; set; }

    [ObservableProperty]
    public partial Uri? ThumbnailUri { get; set; }

    [ObservableProperty]
    public partial string? Collection { get; set; }

    [ObservableProperty]
    public partial DownloadQuality Quality { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsActive), nameof(CanCancel), nameof(CanRetry), nameof(IsCompleted), nameof(IsFailed), nameof(IsProgressIndeterminate))]
    public partial DownloadState State { get; set; }

    /// <summary>Gets the progress as a percentage from 0 to 100.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsProgressIndeterminate))]
    public partial double ProgressPercent { get; set; }

    [ObservableProperty]
    public partial bool HasProgress { get; set; }

    [ObservableProperty]
    public partial long? DownloadedBytes { get; set; }

    [ObservableProperty]
    public partial long? TotalBytes { get; set; }

    [ObservableProperty]
    public partial double? SpeedBytesPerSecond { get; set; }

    [ObservableProperty]
    public partial TimeSpan? Eta { get; set; }

    [ObservableProperty]
    public partial string? FilePath { get; set; }

    [ObservableProperty]
    public partial string? Error { get; set; }

    public bool IsActive => State is DownloadState.Resolving or DownloadState.Downloading or DownloadState.Processing;

    public bool CanCancel => State is DownloadState.Resolving or DownloadState.Queued or DownloadState.Downloading or DownloadState.Processing;

    public bool CanRetry => State is DownloadState.Failed or DownloadState.Canceled;

    public bool IsCompleted => State == DownloadState.Completed;

    public bool IsFailed => State == DownloadState.Failed;

    /// <summary>Gets whether the bar should pulse: link expansion, merging, or the first moments of a transfer.</summary>
    public bool IsProgressIndeterminate => State is DownloadState.Resolving or DownloadState.Processing || (State == DownloadState.Downloading && !HasProgress);

    public DownloadItemViewModel(DownloadJob job)
    {
        Id = job.Id;
        SourceUrl = job.SourceUrl;
        Update(job);
    }

    /// <summary>
    /// Copies the latest values from the helper. Unchanged values raise no notifications.
    /// </summary>
    public void Update(DownloadJob job)
    {
        SourceUrl = job.SourceUrl;
        Title = job.Title;
        Channel = job.Channel;
        Duration = job.DurationSeconds is double seconds ? TimeSpan.FromSeconds(seconds) : null;
        ThumbnailUri = Uri.TryCreate(job.ThumbnailUrl, UriKind.Absolute, out Uri? thumbnail) ? thumbnail : null;
        Collection = job.Collection;
        Quality = job.Quality;
        HasProgress = job.Progress is not null;
        ProgressPercent = (job.Progress ?? 0) * 100;
        State = job.State;
        DownloadedBytes = job.DownloadedBytes;
        TotalBytes = job.TotalBytes;
        SpeedBytesPerSecond = job.SpeedBytesPerSecond;
        Eta = job.EtaSeconds is double eta ? TimeSpan.FromSeconds(eta) : null;
        FilePath = job.FilePath;
        Error = job.Error;
    }
}
