using System.Globalization;
using System.Text.Json;
using Screenbox.Core.Downloads.Contracts;

namespace Screenbox.Downloader;

/// <summary>
/// Wraps yt-dlp: keeps a writable, self-updating copy of it, expands links and downloads single videos.
/// </summary>
/// <remarks>
/// The package install folder is read-only, so the bundled yt-dlp.exe is copied to the state folder and updated
/// there with <c>yt-dlp -U</c>. ffmpeg and Deno never need updating for YouTube changes and run from the package.
/// </remarks>
internal sealed class YtDlpEngine
{
    private const string ProgressPrefix = "SBXP ";
    private const string FilePrefix = "SBXFILE ";
    private const string FormatPrefix = "SBXFMT ";

    private static readonly string[] UnavailableHints =
    [
        "private video",
        "video unavailable",
        "this video is not available",
        "has been removed",
        "sign in to confirm your age",
        "members-only",
        "join this channel",
        "copyright",
        "not available in your country",
        "unsupported url",
        "is not a valid url",
        "drm",
    ];

    private readonly string _root;
    private readonly string _toolsDirectory;
    private readonly Log _log;

    public YtDlpEngine(string root, string toolsDirectory, Log log)
    {
        _root = root;
        _toolsDirectory = toolsDirectory;
        _log = log;
    }

    private string EngineDirectory => Path.Combine(_root, "engine");

    private string EnginePath => Path.Combine(EngineDirectory, "yt-dlp.exe");

    private string BundledEnginePath => Path.Combine(_toolsDirectory, "yt-dlp.exe");

    private string DenoPath => Path.Combine(_toolsDirectory, "deno.exe");

    private string CacheDirectory => Path.Combine(_root, "cache");

    /// <summary>
    /// Copies the bundled yt-dlp into the writable engine folder when there is no copy yet, or when an app update
    /// shipped a newer one than the local copy.
    /// </summary>
    public async Task EnsureInstalledAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(EngineDirectory);
        if (!File.Exists(BundledEnginePath))
        {
            if (File.Exists(EnginePath)) return;
            throw new EngineException("yt-dlp.exe is missing from the app package.", retryable: false);
        }

        string markerPath = Path.Combine(EngineDirectory, "bundled.txt");
        FileInfo bundled = new(BundledEnginePath);
        string marker = $"{bundled.Length}|{bundled.LastWriteTimeUtc.Ticks}";
        string? previousMarker = File.Exists(markerPath) ? await File.ReadAllTextAsync(markerPath, cancellationToken) : null;

        if (File.Exists(EnginePath) && previousMarker == marker) return;

        bool copy = !File.Exists(EnginePath);
        if (!copy)
        {
            // yt-dlp versions are dates (2026.10.26), so ordinal comparison orders them correctly.
            string? local = await GetVersionAsync(EnginePath, cancellationToken);
            string? shipped = await GetVersionAsync(BundledEnginePath, cancellationToken);
            copy = local is null || (shipped is not null && string.CompareOrdinal(shipped, local) > 0);
        }

        if (copy)
        {
            File.Copy(BundledEnginePath, EnginePath, overwrite: true);
            _log.Write("Installed bundled yt-dlp");
        }

        await File.WriteAllTextAsync(markerPath, marker, cancellationToken);
    }

    /// <summary>
    /// Returns the version of the working copy of yt-dlp, or <see langword="null"/> when it cannot run.
    /// </summary>
    public Task<string?> GetVersionAsync(CancellationToken cancellationToken) => GetVersionAsync(EnginePath, cancellationToken);

    private static async Task<string?> GetVersionAsync(string path, CancellationToken cancellationToken)
    {
        string? version = null;
        try
        {
            ProcessRunner.Result result = await ProcessRunner.RunAsync(path, (string[])["--version"], line =>
            {
                if (version is null && !string.IsNullOrWhiteSpace(line)) version = line.Trim();
            }, cancellationToken);
            return result.ExitCode == 0 ? version : null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Updates the working copy of yt-dlp to the latest stable release.
    /// </summary>
    /// <returns>The version after the update attempt.</returns>
    public async Task<string?> UpdateAsync(CancellationToken cancellationToken)
    {
        ProcessRunner.Result result = await ProcessRunner.RunAsync(EnginePath, (string[])["-U", "--no-colors"], line => _log.Write($"[update] {line}"), cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new EngineException(LastErrorLine(result.StandardError) ?? "yt-dlp could not update itself.", retryable: true);
        }

        return await GetVersionAsync(cancellationToken);
    }

    /// <summary>
    /// Expands a link into the videos it contains: one for a video link, every entry for a playlist link.
    /// </summary>
    public async Task<ResolvedLink> ResolveAsync(string url, CancellationToken cancellationToken)
    {
        List<string> arguments =
        [
            "-J",
            "--flat-playlist",
            "--ignore-config",
            "--no-colors",
            "--no-warnings",
            "--js-runtimes", $"deno:{DenoPath}",
            "--cache-dir", CacheDirectory,
        ];

        // A watch link that also carries &list= should fetch the video, not the whole playlist.
        if (IsSingleVideoLink(url)) arguments.Add("--no-playlist");
        arguments.Add(url);

        System.Text.StringBuilder json = new();
        ProcessRunner.Result result = await ProcessRunner.RunAsync(EnginePath, arguments, line =>
        {
            if (line.StartsWith('{')) json.Append(line);
        }, cancellationToken);

        if (result.ExitCode != 0 || json.Length == 0)
        {
            throw Classify(result.StandardError, "Could not read this link.");
        }

        using JsonDocument document = JsonDocument.Parse(json.ToString());
        JsonElement rootElement = document.RootElement;
        string type = GetString(rootElement, "_type") ?? "video";

        if (type is "playlist" or "multi_video")
        {
            List<ResolvedEntry> entries = [];
            if (rootElement.TryGetProperty("entries", out JsonElement entriesElement) && entriesElement.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement entry in entriesElement.EnumerateArray())
                {
                    ResolvedEntry? resolved = ReadEntry(entry);
                    if (resolved is not null) entries.Add(resolved);
                }
            }

            return new ResolvedLink(GetString(rootElement, "title"), entries);
        }

        return new ResolvedLink(null, [ReadEntry(rootElement) ?? new ResolvedEntry(url, null, null, null, null)]);
    }

    /// <summary>
    /// Downloads one video to <paramref name="outputDirectory"/>.
    /// </summary>
    /// <param name="url">The video page.</param>
    /// <param name="quality">The quality preset.</param>
    /// <param name="outputDirectory">Where the finished file goes.</param>
    /// <param name="workDirectory">Where partial files go; deleted by the caller afterwards.</param>
    /// <param name="onProgress">Receives progress updates from a background thread.</param>
    /// <param name="cancellationToken">Cancels and kills yt-dlp.</param>
    /// <returns>The path of the finished file.</returns>
    public async Task<string> DownloadAsync(
        string url,
        DownloadQuality quality,
        string outputDirectory,
        string workDirectory,
        Action<DownloadProgress> onProgress,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(outputDirectory);
        Directory.CreateDirectory(workDirectory);

        List<string> arguments =
        [
            "--ignore-config",
            "--no-colors",
            "--newline",
            "--no-playlist",
            "--no-mtime",
            "--js-runtimes", $"deno:{DenoPath}",
            "--ffmpeg-location", _toolsDirectory,
            "--cache-dir", CacheDirectory,
            "--retries", "10",
            "--fragment-retries", "10",
            "--concurrent-fragments", "4",
            "--embed-metadata",
            "--embed-thumbnail",
            "--embed-chapters",
            "-P", $"home:{outputDirectory}",
            "-P", $"temp:{workDirectory}",
            "-o", "%(title).150B [%(id)s].%(ext)s",
            "--progress",
            "--progress-template", "download:" + ProgressPrefix + "%(progress.downloaded_bytes)s|%(progress.total_bytes)s|%(progress.total_bytes_estimate)s|%(progress.speed)s|%(progress.eta)s|%(info.format_id)s",
            "--print", "before_dl:" + FormatPrefix + "%(format_id)s",
            "--print", "after_move:" + FilePrefix + "%(filepath)s",
        ];
        arguments.AddRange(FormatArguments(quality));
        arguments.Add(url);

        ProgressParser parser = new(quality == DownloadQuality.AudioOnly ? 1 : 2);
        string? filePath = null;

        ProcessRunner.Result result = await ProcessRunner.RunAsync(EnginePath, arguments, line =>
        {
            if (line.StartsWith(ProgressPrefix, StringComparison.Ordinal))
            {
                DownloadProgress? progress = parser.Parse(line[ProgressPrefix.Length..]);
                if (progress is not null) onProgress(progress);
            }
            else if (line.StartsWith(FormatPrefix, StringComparison.Ordinal))
            {
                parser.SetFormat(line[FormatPrefix.Length..].Trim());
            }
            else if (line.StartsWith(FilePrefix, StringComparison.Ordinal))
            {
                filePath = line[FilePrefix.Length..].Trim();
            }
        }, cancellationToken);

        if (result.ExitCode == 0 && filePath is not null && File.Exists(filePath))
        {
            return filePath;
        }

        _log.Write($"yt-dlp failed ({result.ExitCode}) for {url}: {result.StandardError}");
        throw Classify(result.StandardError, "The download failed.");
    }

    private static string[] FormatArguments(DownloadQuality quality)
    {
        if (quality == DownloadQuality.AudioOnly)
        {
            // M4A remuxes without re-encoding for almost every YouTube upload.
            return ["-f", "ba[ext=m4a]/ba/b", "-x", "--audio-format", "m4a"];
        }

        // Prefer VP9 or H.264 over AV1 for hardware decoding on older PCs, and AAC so MP4 needs no re-encode.
        string resolution = quality switch
        {
            DownloadQuality.Max1080p => "res:1080",
            DownloadQuality.Max720p => "res:720",
            DownloadQuality.Max480p => "res:480",
            _ => "res",
        };

        return ["-f", "bv*+ba/b", "-S", $"{resolution},fps,vcodec:vp9,acodec:aac", "--merge-output-format", "mp4"];
    }

    /// <summary>
    /// Whether a link points at one video even if it also names a playlist.
    /// </summary>
    internal static bool IsSingleVideoLink(string url)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out Uri? uri)) return false;
        string host = uri.Host.ToLowerInvariant();
        if (host.EndsWith("youtu.be", StringComparison.Ordinal)) return true;
        if (uri.AbsolutePath.StartsWith("/shorts/", StringComparison.OrdinalIgnoreCase)) return true;
        return uri.Query.Contains("v=", StringComparison.Ordinal) && uri.AbsolutePath.StartsWith("/watch", StringComparison.OrdinalIgnoreCase);
    }

    private static ResolvedEntry? ReadEntry(JsonElement entry)
    {
        if (entry.ValueKind != JsonValueKind.Object) return null;

        string? id = GetString(entry, "id");
        string? url = GetString(entry, "webpage_url") ?? GetString(entry, "url");
        string extractor = GetString(entry, "ie_key") ?? GetString(entry, "extractor_key") ?? string.Empty;
        bool isYouTube = extractor.StartsWith("Youtube", StringComparison.OrdinalIgnoreCase);

        if ((url is null || !url.Contains("://", StringComparison.Ordinal)) && id is not null && isYouTube)
        {
            url = $"https://www.youtube.com/watch?v={id}";
        }

        if (url is null) return null;

        string? thumbnail = GetString(entry, "thumbnail");
        if (thumbnail is null && entry.TryGetProperty("thumbnails", out JsonElement thumbnails) && thumbnails.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in thumbnails.EnumerateArray())
            {
                thumbnail = GetString(item, "url") ?? thumbnail;
            }
        }

        if (thumbnail is null && id is not null && isYouTube)
        {
            thumbnail = $"https://i.ytimg.com/vi/{id}/hqdefault.jpg";
        }

        double? duration = entry.TryGetProperty("duration", out JsonElement durationElement) && durationElement.ValueKind == JsonValueKind.Number
            ? durationElement.GetDouble()
            : null;

        return new ResolvedEntry(
            url,
            GetString(entry, "title"),
            GetString(entry, "channel") ?? GetString(entry, "uploader"),
            duration,
            thumbnail);
    }

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string? LastErrorLine(string standardError) =>
        standardError.Split('\n')
            .Select(line => line.Trim())
            .LastOrDefault(line => line.StartsWith("ERROR:", StringComparison.Ordinal))?["ERROR:".Length..].Trim();

    private static EngineException Classify(string standardError, string fallback)
    {
        string message = LastErrorLine(standardError) ?? fallback;
        string lower = standardError.ToLowerInvariant();
        bool unavailable = UnavailableHints.Any(lower.Contains);
        return new EngineException(message, retryable: !unavailable);
    }

    /// <summary>
    /// Turns yt-dlp's per-stream progress into one overall value for a video that is downloaded as two streams.
    /// </summary>
    private sealed class ProgressParser
    {
        // The video stream is usually most of the bytes; the audio stream gets the last slice of the bar.
        private const double FirstPartWeight = 0.85;

        private int _expectedParts;
        private readonly List<string> _seenFormats = [];

        public ProgressParser(int expectedParts)
        {
            _expectedParts = expectedParts;
        }

        public void SetFormat(string formatId)
        {
            // "248+140" means two streams merged afterwards; "18" means a single muxed stream.
            _expectedParts = Math.Max(1, formatId.Split('+', StringSplitOptions.RemoveEmptyEntries).Length);
        }

        public DownloadProgress? Parse(string payload)
        {
            string[] parts = payload.Split('|');
            if (parts.Length < 6) return null;

            long? downloaded = ParseLong(parts[0]);
            long? total = ParseLong(parts[1]) ?? ParseLong(parts[2]);
            double? speed = ParseDouble(parts[3]);
            double? eta = ParseDouble(parts[4]);
            string format = parts[5];

            if (!_seenFormats.Contains(format)) _seenFormats.Add(format);
            int partIndex = Math.Min(_seenFormats.Count, _expectedParts) - 1;

            double? partFraction = downloaded is not null && total is > 0 ? Math.Clamp((double)downloaded.Value / total.Value, 0, 1) : null;
            double? overall = null;
            if (partFraction is not null)
            {
                if (_expectedParts <= 1)
                {
                    overall = partFraction;
                }
                else
                {
                    double start = partIndex == 0 ? 0 : FirstPartWeight;
                    double width = partIndex == 0 ? FirstPartWeight : 1 - FirstPartWeight;
                    overall = start + (width * partFraction.Value);
                }
            }

            bool lastPartDone = partIndex == _expectedParts - 1 && partFraction >= 1;
            return new DownloadProgress(overall, downloaded, total, speed, eta, lastPartDone);
        }

        private static long? ParseLong(string value) =>
            double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double result) ? (long)result : null;

        private static double? ParseDouble(string value) =>
            double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double result) ? result : null;
    }
}

/// <summary>
/// A link expanded by yt-dlp.
/// </summary>
/// <param name="CollectionTitle">The playlist title, or <see langword="null"/> for a single video.</param>
/// <param name="Entries">The videos in the link.</param>
internal sealed record ResolvedLink(string? CollectionTitle, List<ResolvedEntry> Entries);

/// <summary>
/// One video found in a link.
/// </summary>
internal sealed record ResolvedEntry(string Url, string? Title, string? Channel, double? DurationSeconds, string? ThumbnailUrl);

/// <summary>
/// A progress report for the current download.
/// </summary>
/// <param name="Overall">Overall progress between 0 and 1, when known.</param>
/// <param name="DownloadedBytes">Bytes of the current stream received so far.</param>
/// <param name="TotalBytes">Size of the current stream, exact or estimated.</param>
/// <param name="Speed">Bytes per second.</param>
/// <param name="Eta">Seconds left for the current stream.</param>
/// <param name="TransferFinished">Whether the last stream is complete and only post-processing remains.</param>
internal sealed record DownloadProgress(double? Overall, long? DownloadedBytes, long? TotalBytes, double? Speed, double? Eta, bool TransferFinished);

/// <summary>
/// A yt-dlp failure. <see cref="Retryable"/> is <see langword="false"/> when the video itself cannot be downloaded
/// (private, removed, region locked), so updating yt-dlp would not help.
/// </summary>
internal sealed class EngineException(string message, bool retryable) : Exception(message)
{
    public bool Retryable { get; } = retryable;
}
