using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using Screenbox.Core.Contexts;
using Screenbox.Core.Downloads.Contracts;
using Screenbox.Core.Enums;
using Screenbox.Core.Messages;
using Screenbox.Core.Services;
using Screenbox.Core.ViewModels;
using Windows.System;

namespace Screenbox.Core.Coordinators;

/// <summary>
/// Owns the polling timer that mirrors the helper's queue file into <see cref="DownloadsContext"/>, and restarts
/// the helper whenever there is pending work and no live helper.
/// </summary>
public sealed partial class DownloadsCoordinator : IDownloadsCoordinator
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(750);
    private static readonly TimeSpan RelaunchCooldown = TimeSpan.FromSeconds(8);

    private readonly DownloadsContext _context;
    private readonly IDownloaderService _downloaderService;
    private readonly ILogger<DownloadsCoordinator> _logger;
    private readonly DispatcherQueueTimer _pollTimer;
    private readonly Dictionary<string, DownloadState> _lastStates = new();

    private DownloaderSnapshot? _lastSnapshot;
    private DateTimeOffset _lastLaunch = DateTimeOffset.MinValue;
    private bool _started;
    private bool _polling;

    public DownloadsCoordinator(DownloadsContext context, IDownloaderService downloaderService, ILogger<DownloadsCoordinator> logger)
    {
        _context = context;
        _downloaderService = downloaderService;
        _logger = logger;
        _context.IsSupported = downloaderService.IsSupported;

        _pollTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _pollTimer.Interval = PollInterval;
        _pollTimer.IsRepeating = true;
        _pollTimer.Tick += (_, _) => _ = PollAsync();
    }

    /// <inheritdoc/>
    public async Task StartAsync()
    {
        if (_started || !_downloaderService.IsSupported) return;
        _started = true;

        // The first poll also relaunches the helper if downloads were interrupted by closing the app.
        await PollAsync();
        _pollTimer.Start();
    }

    /// <inheritdoc/>
    public Task AddAsync(string url, DownloadQuality quality) =>
        SendAsync(new DownloaderCommand { Kind = DownloaderCommandKind.Add, Url = url, Quality = quality });

    /// <inheritdoc/>
    public Task CancelAsync(string jobId) =>
        SendAsync(new DownloaderCommand { Kind = DownloaderCommandKind.Cancel, JobId = jobId });

    /// <inheritdoc/>
    public Task RetryAsync(string jobId) =>
        SendAsync(new DownloaderCommand { Kind = DownloaderCommandKind.Retry, JobId = jobId });

    /// <inheritdoc/>
    public Task RemoveAsync(string jobId) =>
        SendAsync(new DownloaderCommand { Kind = DownloaderCommandKind.Remove, JobId = jobId });

    /// <inheritdoc/>
    public Task ClearFinishedAsync() =>
        SendAsync(new DownloaderCommand { Kind = DownloaderCommandKind.ClearFinished });

    /// <inheritdoc/>
    public Task UpdateEngineAsync() =>
        SendAsync(new DownloaderCommand { Kind = DownloaderCommandKind.UpdateEngine });

    /// <inheritdoc/>
    public void Dispose()
    {
        _pollTimer.Stop();
    }

    private async Task SendAsync(DownloaderCommand command)
    {
        if (!_downloaderService.IsSupported) return;

        await _downloaderService.SendCommandAsync(command);
        await StartAsync();

        // Commands sit on disk until a helper reads them, so make sure one is running.
        if (!IsHelperAlive(_lastSnapshot))
        {
            await LaunchAsync(force: true);
        }
    }

    private async Task PollAsync()
    {
        if (_polling) return;
        _polling = true;
        try
        {
            DownloaderSnapshot? snapshot = await _downloaderService.ReadSnapshotAsync();
            if (snapshot is null) return;

            _lastSnapshot = snapshot;
            Apply(snapshot);

            bool hasPending = snapshot.Jobs.Any(job => IsPending(job.State));
            if (hasPending && !IsHelperAlive(snapshot))
            {
                await LaunchAsync(force: false);
            }
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "Download queue poll failed");
        }
        finally
        {
            _polling = false;
        }
    }

    private async Task LaunchAsync(bool force)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (!force && now - _lastLaunch < RelaunchCooldown) return;

        _lastLaunch = now;
        _context.HelperLaunchFailed = !await _downloaderService.LaunchHelperAsync();
    }

    private static bool IsHelperAlive(DownloaderSnapshot? snapshot) =>
        snapshot is { IsRunning: true } && DateTimeOffset.UtcNow - snapshot.Heartbeat < DownloaderPaths.HeartbeatTimeout;

    private static bool IsPending(DownloadState state) =>
        state is DownloadState.Resolving or DownloadState.Queued or DownloadState.Downloading or DownloadState.Processing;

    /// <summary>
    /// Merges a snapshot into the observable list, moving and updating existing items instead of recreating them.
    /// </summary>
    private void Apply(DownloaderSnapshot snapshot)
    {
        _context.EngineVersion = snapshot.EngineVersion;
        _context.EngineError = snapshot.EngineError;
        _context.IsUpdatingEngine = snapshot.IsUpdatingEngine;

        IList<DownloadItemViewModel> items = _context.Items;
        Dictionary<string, DownloadItemViewModel> existing = items.ToDictionary(item => item.Id);
        HashSet<string> seen = new();

        for (int index = 0; index < snapshot.Jobs.Count; index++)
        {
            DownloadJob job = snapshot.Jobs[index];
            seen.Add(job.Id);

            if (existing.TryGetValue(job.Id, out DownloadItemViewModel? item))
            {
                item.Update(job);
                int currentIndex = items.IndexOf(item);
                if (currentIndex != index)
                {
                    _context.Items.Move(currentIndex, index);
                }
            }
            else
            {
                items.Insert(Math.Min(index, items.Count), new DownloadItemViewModel(job));
            }

            NotifyTransition(job);
        }

        for (int index = items.Count - 1; index >= 0; index--)
        {
            if (!seen.Contains(items[index].Id))
            {
                _lastStates.Remove(items[index].Id);
                items.RemoveAt(index);
            }
        }

        _context.PendingCount = snapshot.Jobs.Count(job => IsPending(job.State));
        _context.FinishedCount = snapshot.Jobs.Count - _context.PendingCount;
    }

    /// <summary>
    /// Shows an in-app notification when a job finishes while the app is watching. Jobs that were already finished
    /// when the app started are not announced again.
    /// </summary>
    private void NotifyTransition(DownloadJob job)
    {
        bool known = _lastStates.TryGetValue(job.Id, out DownloadState previous);
        _lastStates[job.Id] = job.State;
        if (!known || previous == job.State) return;

        if (job.State == DownloadState.Completed)
        {
            WeakReferenceMessenger.Default.Send(new NotificationMessage(NotificationLevel.Success, NotificationKind.DownloadCompleted, message: job.Title ?? job.SourceUrl));
        }
        else if (job.State == DownloadState.Failed)
        {
            string message = job.Title is null ? job.Error ?? string.Empty : $"{job.Title}{Environment.NewLine}{job.Error}";
            WeakReferenceMessenger.Default.Send(new NotificationMessage(NotificationLevel.Error, NotificationKind.DownloadFailed, message: message));
        }
    }
}
