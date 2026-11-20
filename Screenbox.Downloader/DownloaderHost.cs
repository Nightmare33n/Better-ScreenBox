using System.Text.Json;
using Screenbox.Core.Downloads.Contracts;

namespace Screenbox.Downloader;

/// <summary>
/// Owns the download queue: applies commands from the app, runs one job at a time and publishes the state.
/// </summary>
/// <remarks>
/// The helper is the only writer of the queue file, which keeps the two processes from fighting over it. Jobs that
/// were running when the helper died are put back in the queue on the next start, so downloads survive the app
/// closing, a crash or a reboot.
/// </remarks>
internal sealed class DownloaderHost
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan IdleExitDelay = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan EngineUpdateInterval = TimeSpan.FromHours(24);

    private readonly string _root;
    private readonly Log _log;
    private readonly YtDlpEngine _engine;
    private readonly Lock _lock = new();

    private DownloaderSnapshot _snapshot = new();
    private bool _dirty = true;
    private DateTimeOffset _lastSave = DateTimeOffset.MinValue;

    private string? _activeJobId;
    private CancellationTokenSource? _activeCancellation;
    private bool _workerBusy;
    private bool _engineUpdateRequested;
    private bool _engineReady;
    private bool _updatedThisSession;
    private CancellationToken _stopping;

    public DownloaderHost(string root, string toolsDirectory, Log log)
    {
        _root = root;
        _log = log;
        _engine = new YtDlpEngine(root, toolsDirectory, log);
    }

    private string SnapshotPath => Path.Combine(_root, DownloaderPaths.SnapshotFileName);

    private string CommandsPath => Path.Combine(_root, DownloaderPaths.CommandsFolderName);

    private string WorkRoot => Path.Combine(_root, "work");

    /// <summary>
    /// Runs until there has been nothing to do for a while.
    /// </summary>
    public async Task RunAsync()
    {
        Directory.CreateDirectory(CommandsPath);
        LoadSnapshot();
        CleanWorkFolders();

        using CancellationTokenSource stopping = new();
        _stopping = stopping.Token;
        Task worker = Task.Run(() => WorkerLoopAsync(stopping.Token));
        DateTimeOffset idleSince = DateTimeOffset.UtcNow;

        while (true)
        {
            bool hadCommands = ApplyCommands();
            SaveIfNeeded();

            if (hadCommands || HasWork())
            {
                idleSince = DateTimeOffset.UtcNow;
            }
            else if (DateTimeOffset.UtcNow - idleSince > IdleExitDelay)
            {
                break;
            }

            if (worker.IsFaulted)
            {
                _log.Write($"Worker crashed: {worker.Exception}");
                break;
            }

            await Task.Delay(TickInterval);
        }

        await stopping.CancelAsync();
        try
        {
            await worker;
        }
        catch (OperationCanceledException)
        {
        }

        lock (_lock)
        {
            _snapshot.IsRunning = false;
            _dirty = true;
        }

        SaveIfNeeded(force: true);
    }

    private bool HasWork()
    {
        lock (_lock)
        {
            return _workerBusy || _engineUpdateRequested || _snapshot.Jobs.Any(job => job.State is DownloadState.Resolving or DownloadState.Queued);
        }
    }

    private async Task WorkerLoopAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            bool didWork = await RunNextAsync(stoppingToken);
            if (!didWork)
            {
                await Task.Delay(TickInterval, stoppingToken);
            }
        }
    }

    /// <summary>
    /// Runs one unit of work: an engine update, a link expansion or a download.
    /// </summary>
    /// <returns><see langword="true"/> when something ran.</returns>
    private async Task<bool> RunNextAsync(CancellationToken stoppingToken)
    {
        DownloadJob? job;
        bool updateEngine;
        lock (_lock)
        {
            updateEngine = _engineUpdateRequested;
            job = _snapshot.Jobs.FirstOrDefault(j => j.State == DownloadState.Resolving)
                ?? _snapshot.Jobs.FirstOrDefault(j => j.State == DownloadState.Queued);
            if (!updateEngine && job is null) return false;

            _workerBusy = true;
        }

        try
        {
            if (!await EnsureEngineAsync(stoppingToken))
            {
                FailPending();
                return true;
            }

            if (updateEngine)
            {
                await UpdateEngineAsync(stoppingToken);
                return true;
            }

            if (job is null) return true;

            using CancellationTokenSource jobCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            lock (_lock)
            {
                _activeJobId = job.Id;
                _activeCancellation = jobCancellation;
            }

            if (job.State == DownloadState.Resolving)
            {
                await ResolveAsync(job, jobCancellation.Token);
            }
            else
            {
                await DownloadAsync(job, jobCancellation.Token);
            }

            return true;
        }
        finally
        {
            lock (_lock)
            {
                _activeJobId = null;
                _activeCancellation = null;
                _workerBusy = false;
                _dirty = true;
            }
        }
    }

    private async Task<bool> EnsureEngineAsync(CancellationToken cancellationToken)
    {
        if (_engineReady) return true;

        try
        {
            await _engine.EnsureInstalledAsync(cancellationToken);
            string? version = await _engine.GetVersionAsync(cancellationToken);
            if (version is null)
            {
                throw new EngineException("yt-dlp could not start on this PC.", retryable: false);
            }

            DateTimeOffset? checkedAt;
            lock (_lock)
            {
                _snapshot.EngineVersion = version;
                _snapshot.EngineError = null;
                checkedAt = _snapshot.EngineCheckedAt;
                _dirty = true;
            }

            _engineReady = true;

            // YouTube changes often break old extractors: refresh before the first download of the day.
            if (checkedAt is null || DateTimeOffset.UtcNow - checkedAt > EngineUpdateInterval)
            {
                await UpdateEngineAsync(cancellationToken);
            }

            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            _log.Write($"Engine unavailable: {e}");
            lock (_lock)
            {
                _snapshot.EngineError = e.Message;
                _dirty = true;
            }

            return false;
        }
    }

    private async Task UpdateEngineAsync(CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            _engineUpdateRequested = false;
            _snapshot.IsUpdatingEngine = true;
            _dirty = true;
        }

        try
        {
            string? version = await _engine.UpdateAsync(cancellationToken);
            _updatedThisSession = true;
            lock (_lock)
            {
                _snapshot.EngineVersion = version ?? _snapshot.EngineVersion;
                _snapshot.EngineError = null;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            // A failed update is not fatal: the current yt-dlp keeps working for most videos.
            _log.Write($"Engine update failed: {e.Message}");
        }
        finally
        {
            lock (_lock)
            {
                _snapshot.IsUpdatingEngine = false;
                _snapshot.EngineCheckedAt = DateTimeOffset.UtcNow;
                _dirty = true;
            }
        }
    }

    private async Task ResolveAsync(DownloadJob placeholder, CancellationToken cancellationToken)
    {
        try
        {
            ResolvedLink link = await _engine.ResolveAsync(placeholder.SourceUrl, cancellationToken);
            lock (_lock)
            {
                int index = _snapshot.Jobs.IndexOf(placeholder);
                if (index < 0) return;

                if (link.Entries.Count == 0)
                {
                    Fail(placeholder, "No videos were found in this link.");
                    return;
                }

                if (link.CollectionTitle is null && link.Entries.Count == 1)
                {
                    Apply(placeholder, link.Entries[0], null);
                    placeholder.State = DownloadState.Queued;
                }
                else
                {
                    // A playlist replaces its placeholder with one job per entry, in playlist order.
                    _snapshot.Jobs.RemoveAt(index);
                    List<DownloadJob> jobs = link.Entries.Select(entry =>
                    {
                        DownloadJob job = new()
                        {
                            Id = Guid.NewGuid().ToString("N"),
                            Quality = placeholder.Quality,
                            State = DownloadState.Queued,
                            CreatedAt = DateTimeOffset.UtcNow,
                        };
                        Apply(job, entry, link.CollectionTitle);
                        return job;
                    }).ToList();
                    _snapshot.Jobs.InsertRange(index, jobs);
                }

                _dirty = true;
            }
        }
        catch (OperationCanceledException) when (!_stopping.IsCancellationRequested)
        {
            MarkCanceled(placeholder);
        }
        catch (EngineException e)
        {
            lock (_lock)
            {
                Fail(placeholder, e.Message);
            }
        }
    }

    private async Task DownloadAsync(DownloadJob job, CancellationToken cancellationToken)
    {
        string workDirectory = Path.Combine(WorkRoot, job.Id);
        lock (_lock)
        {
            job.State = DownloadState.Downloading;
            job.Progress = null;
            job.Error = null;
            _dirty = true;
        }

        try
        {
            string filePath;
            try
            {
                filePath = await RunDownloadAsync(job, workDirectory, cancellationToken);
            }
            catch (EngineException e) when (e.Retryable && !_updatedThisSession)
            {
                // Most "it suddenly stopped working" failures are fixed by a newer yt-dlp: update once, then retry.
                _log.Write($"Retrying {job.SourceUrl} after an engine update: {e.Message}");
                await UpdateEngineAsync(cancellationToken);
                filePath = await RunDownloadAsync(job, workDirectory, cancellationToken);
            }

            lock (_lock)
            {
                job.State = DownloadState.Completed;
                job.FilePath = filePath;
                job.Progress = 1;
                job.SpeedBytesPerSecond = null;
                job.EtaSeconds = null;
                job.FinishedAt = DateTimeOffset.UtcNow;
                _dirty = true;
            }

            _log.Write($"Completed {filePath}");
        }
        catch (OperationCanceledException) when (!_stopping.IsCancellationRequested)
        {
            MarkCanceled(job);
        }
        catch (OperationCanceledException)
        {
            // The helper is shutting down: put the job back so the next start resumes it.
            lock (_lock)
            {
                job.State = DownloadState.Queued;
                _dirty = true;
            }

            throw;
        }
        catch (Exception e) when (e is EngineException or IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            lock (_lock)
            {
                Fail(job, e.Message);
            }
        }
        finally
        {
            TryDeleteDirectory(workDirectory);
        }
    }

    private Task<string> RunDownloadAsync(DownloadJob job, string workDirectory, CancellationToken cancellationToken)
    {
        string outputDirectory = GetOutputDirectory(job);
        return _engine.DownloadAsync(job.SourceUrl, job.Quality, outputDirectory, workDirectory, progress =>
        {
            lock (_lock)
            {
                job.State = progress.TransferFinished ? DownloadState.Processing : DownloadState.Downloading;
                job.Progress = progress.Overall ?? job.Progress;
                job.DownloadedBytes = progress.DownloadedBytes;
                job.TotalBytes = progress.TotalBytes;
                job.SpeedBytesPerSecond = progress.TransferFinished ? null : progress.Speed;
                job.EtaSeconds = progress.TransferFinished ? null : progress.Eta;
                _dirty = true;
            }
        }, cancellationToken);
    }

    private static string GetOutputDirectory(DownloadJob job)
    {
        Environment.SpecialFolder folder = job.Quality == DownloadQuality.AudioOnly
            ? Environment.SpecialFolder.MyMusic
            : Environment.SpecialFolder.MyVideos;
        string directory = Path.Combine(Environment.GetFolderPath(folder), "Screenbox");
        string? collection = SanitizeFolderName(job.Collection);
        return collection is null ? directory : Path.Combine(directory, collection);
    }

    private static string? SanitizeFolderName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        char[] invalid = Path.GetInvalidFileNameChars();
        string clean = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
        if (clean.Length > 100) clean = clean[..100].Trim();
        return clean.Length == 0 ? null : clean;
    }

    private static void Apply(DownloadJob job, ResolvedEntry entry, string? collection)
    {
        job.SourceUrl = entry.Url;
        job.Title = entry.Title;
        job.Channel = entry.Channel;
        job.DurationSeconds = entry.DurationSeconds;
        job.ThumbnailUrl = entry.ThumbnailUrl;
        job.Collection = collection;
        job.IsResolved = true;
    }

    private void Fail(DownloadJob job, string message)
    {
        job.State = DownloadState.Failed;
        job.Error = message;
        job.SpeedBytesPerSecond = null;
        job.EtaSeconds = null;
        job.FinishedAt = DateTimeOffset.UtcNow;
        _dirty = true;
    }

    private void MarkCanceled(DownloadJob job)
    {
        lock (_lock)
        {
            job.State = DownloadState.Canceled;
            job.SpeedBytesPerSecond = null;
            job.EtaSeconds = null;
            job.FinishedAt = DateTimeOffset.UtcNow;
            _dirty = true;
        }
    }

    private void FailPending()
    {
        lock (_lock)
        {
            string message = _snapshot.EngineError ?? "The download engine is not available.";
            foreach (DownloadJob job in _snapshot.Jobs.Where(j => j.State is DownloadState.Resolving or DownloadState.Queued))
            {
                Fail(job, message);
            }

            _engineUpdateRequested = false;
        }
    }

    /// <summary>
    /// Reads every pending command file in arrival order, applies it and deletes it.
    /// </summary>
    /// <returns><see langword="true"/> when at least one command was applied.</returns>
    private bool ApplyCommands()
    {
        string[] files;
        try
        {
            files = Directory.GetFiles(CommandsPath, "*.json");
        }
        catch (IOException)
        {
            return false;
        }

        Array.Sort(files, StringComparer.Ordinal);
        bool applied = false;
        foreach (string file in files)
        {
            DownloaderCommand? command;
            try
            {
                command = JsonSerializer.Deserialize(File.ReadAllText(file), DownloaderJsonContext.Default.DownloaderCommand);
            }
            catch (IOException)
            {
                // Still being written; pick it up on the next tick.
                continue;
            }
            catch (JsonException e)
            {
                _log.Write($"Dropping unreadable command {Path.GetFileName(file)}: {e.Message}");
                command = null;
            }

            if (command is not null)
            {
                Apply(command);
                applied = true;
            }

            try
            {
                File.Delete(file);
            }
            catch (IOException)
            {
            }
        }

        return applied;
    }

    private void Apply(DownloaderCommand command)
    {
        lock (_lock)
        {
            DownloadJob? job = command.JobId is null ? null : _snapshot.Jobs.FirstOrDefault(j => j.Id == command.JobId);
            switch (command.Kind)
            {
                case DownloaderCommandKind.Add when !string.IsNullOrWhiteSpace(command.Url):
                    _snapshot.Jobs.Add(new DownloadJob
                    {
                        Id = Guid.NewGuid().ToString("N"),
                        SourceUrl = command.Url.Trim(),
                        Quality = command.Quality,
                        State = DownloadState.Resolving,
                        CreatedAt = DateTimeOffset.UtcNow,
                    });
                    break;

                case DownloaderCommandKind.Cancel when job is not null:
                    if (job.Id == _activeJobId)
                    {
                        _activeCancellation?.Cancel();
                    }
                    else if (job.State is DownloadState.Queued or DownloadState.Resolving)
                    {
                        job.State = DownloadState.Canceled;
                        job.FinishedAt = DateTimeOffset.UtcNow;
                    }

                    break;

                case DownloaderCommandKind.Retry when job is not null && job.State is DownloadState.Failed or DownloadState.Canceled:
                    job.State = job.IsResolved ? DownloadState.Queued : DownloadState.Resolving;
                    job.Error = null;
                    job.Progress = null;
                    job.FinishedAt = null;
                    break;

                case DownloaderCommandKind.Remove when job is not null:
                    if (job.Id == _activeJobId) _activeCancellation?.Cancel();
                    _snapshot.Jobs.Remove(job);
                    break;

                case DownloaderCommandKind.ClearFinished:
                    _snapshot.Jobs.RemoveAll(j => j.State is DownloadState.Completed or DownloadState.Failed or DownloadState.Canceled);
                    break;

                case DownloaderCommandKind.UpdateEngine:
                    _engineUpdateRequested = true;
                    break;
            }

            _dirty = true;
        }
    }

    private void LoadSnapshot()
    {
        try
        {
            if (File.Exists(SnapshotPath))
            {
                _snapshot = JsonSerializer.Deserialize(File.ReadAllText(SnapshotPath), DownloaderJsonContext.Default.DownloaderSnapshot) ?? new DownloaderSnapshot();
            }
        }
        catch (Exception e) when (e is IOException or JsonException)
        {
            _log.Write($"Starting with an empty queue: {e.Message}");
            _snapshot = new DownloaderSnapshot();
        }

        // Anything that was running when the previous instance stopped starts over.
        foreach (DownloadJob job in _snapshot.Jobs)
        {
            if (job.State is DownloadState.Downloading or DownloadState.Processing)
            {
                job.State = DownloadState.Queued;
                job.SpeedBytesPerSecond = null;
                job.EtaSeconds = null;
            }
        }

        _snapshot.IsRunning = true;
        _snapshot.IsUpdatingEngine = false;
        _dirty = true;
    }

    private void SaveIfNeeded(bool force = false)
    {
        string json;
        lock (_lock)
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            if (!force && !_dirty && now - _lastSave < HeartbeatInterval) return;

            _snapshot.Heartbeat = now;
            json = JsonSerializer.Serialize(_snapshot, DownloaderJsonContext.Default.DownloaderSnapshot);
            _dirty = false;
            _lastSave = now;
        }

        string temporaryPath = SnapshotPath + ".tmp";
        try
        {
            File.WriteAllText(temporaryPath, json);
            // Replace in one step so the app never reads a half-written file.
            File.Move(temporaryPath, SnapshotPath, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // The app may be reading the file right now; try again on the next tick.
            lock (_lock)
            {
                _dirty = true;
            }
        }
    }

    private void CleanWorkFolders()
    {
        if (!Directory.Exists(WorkRoot)) return;
        foreach (string directory in Directory.GetDirectories(WorkRoot))
        {
            TryDeleteDirectory(directory);
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}
