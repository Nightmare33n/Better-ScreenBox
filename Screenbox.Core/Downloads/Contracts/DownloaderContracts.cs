// This file is compiled into both Screenbox.Core and the full-trust Screenbox.Downloader helper.
// It must only depend on the base class library so the helper can stay small and Native AOT friendly.

using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Screenbox.Core.Downloads.Contracts;

/// <summary>
/// The quality preset requested for a download.
/// </summary>
public enum DownloadQuality
{
    Best,
    Max1080p,
    Max720p,
    Max480p,
    AudioOnly,
}

/// <summary>
/// The lifecycle state of a download job.
/// </summary>
public enum DownloadState
{
    /// <summary>The link is being expanded into one or more videos.</summary>
    Resolving,

    /// <summary>Waiting for its turn.</summary>
    Queued,

    /// <summary>Streams are being transferred.</summary>
    Downloading,

    /// <summary>Streams are being merged and tagged.</summary>
    Processing,

    Completed,
    Failed,
    Canceled,
}

/// <summary>
/// The kind of request the app sends to the helper.
/// </summary>
public enum DownloaderCommandKind
{
    Add,
    Cancel,
    Retry,
    Remove,
    ClearFinished,
    UpdateEngine,
}

/// <summary>
/// One video (or audio track) tracked by the helper.
/// </summary>
public sealed class DownloadJob
{
    public string Id { get; set; } = string.Empty;

    /// <summary>The link that was pasted or the video page of a playlist entry.</summary>
    public string SourceUrl { get; set; } = string.Empty;

    public string? Title { get; set; }

    public string? Channel { get; set; }

    public double? DurationSeconds { get; set; }

    public string? ThumbnailUrl { get; set; }

    /// <summary>The playlist the job came from, used as a sub-folder name.</summary>
    public string? Collection { get; set; }

    public DownloadQuality Quality { get; set; }

    /// <summary>
    /// <see langword="false"/> while the job is still the raw pasted link, which may expand into a whole playlist.
    /// </summary>
    public bool IsResolved { get; set; }

    public DownloadState State { get; set; }

    /// <summary>Overall progress between 0 and 1, or <see langword="null"/> while unknown.</summary>
    public double? Progress { get; set; }

    public long? DownloadedBytes { get; set; }

    public long? TotalBytes { get; set; }

    public double? SpeedBytesPerSecond { get; set; }

    public double? EtaSeconds { get; set; }

    /// <summary>The final file on disk once <see cref="State"/> is <see cref="DownloadState.Completed"/>.</summary>
    public string? FilePath { get; set; }

    public string? Error { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? FinishedAt { get; set; }
}

/// <summary>
/// The full state written by the helper. The helper is the only writer, the app only reads it.
/// </summary>
public sealed class DownloaderSnapshot
{
    /// <summary>Updated at least every few seconds while the helper runs, so the app can detect a dead helper.</summary>
    public DateTimeOffset Heartbeat { get; set; }

    /// <summary><see langword="false"/> when the helper wrote its last snapshot before exiting.</summary>
    public bool IsRunning { get; set; }

    public string? EngineVersion { get; set; }

    public string? EngineError { get; set; }

    public bool IsUpdatingEngine { get; set; }

    public DateTimeOffset? EngineCheckedAt { get; set; }

    public List<DownloadJob> Jobs { get; set; } = [];
}

/// <summary>
/// A request from the app. Each command is written to its own file and deleted by the helper once applied.
/// </summary>
public sealed class DownloaderCommand
{
    public DownloaderCommandKind Kind { get; set; }

    public string? Url { get; set; }

    public DownloadQuality Quality { get; set; }

    public string? JobId { get; set; }
}

/// <summary>
/// File and folder names shared by both processes, relative to the package LocalState folder.
/// </summary>
public static class DownloaderPaths
{
    public const string RootFolderName = "Downloads";
    public const string SnapshotFileName = "queue.json";
    public const string CommandsFolderName = "commands";
    public const string HelperFolderName = "Downloader";
    public const string HelperExecutableName = "Screenbox.Downloader.exe";

    /// <summary>How old a heartbeat may be before the app treats the helper as gone.</summary>
    public static readonly TimeSpan HeartbeatTimeout = TimeSpan.FromSeconds(10);
}

[JsonSourceGenerationOptions(
    UseStringEnumConverter = true,
    WriteIndented = false,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(DownloaderSnapshot))]
[JsonSerializable(typeof(DownloaderCommand))]
public sealed partial class DownloaderJsonContext : JsonSerializerContext
{
}
