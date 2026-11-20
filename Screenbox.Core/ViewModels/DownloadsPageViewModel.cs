using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using Screenbox.Core.Contexts;
using Screenbox.Core.Coordinators;
using Screenbox.Core.Downloads.Contracts;
using Screenbox.Core.Enums;
using Screenbox.Core.Factories;
using Screenbox.Core.Helpers;
using Screenbox.Core.Messages;
using Screenbox.Core.Services;
using Windows.Storage;

namespace Screenbox.Core.ViewModels;

/// <summary>
/// Presentation logic for the Downloads page: paste a link, pick a quality, and manage the queue.
/// </summary>
public sealed partial class DownloadsPageViewModel : ObservableRecipient
{
    private readonly IDownloadsCoordinator _coordinator;
    private readonly IFilesService _filesService;
    private readonly MediaViewModelFactory _mediaFactory;
    private readonly ILogger<DownloadsPageViewModel> _logger;

    public DownloadsContext Downloads { get; }

    public ObservableCollection<DownloadItemViewModel> Items => Downloads.Items;

    /// <summary>Gets the quality presets in the order the quality picker shows them.</summary>
    public DownloadQuality[] Qualities { get; } =
    [
        DownloadQuality.Best,
        DownloadQuality.Max1080p,
        DownloadQuality.Max720p,
        DownloadQuality.Max480p,
        DownloadQuality.AudioOnly,
    ];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddCommand))]
    [NotifyPropertyChangedFor(nameof(IsUrlInvalid))]
    public partial string Url { get; set; } = string.Empty;

    [ObservableProperty]
    public partial int SelectedQualityIndex { get; set; } = 1;

    /// <summary>Gets whether the text box holds something that is clearly not a web link.</summary>
    public bool IsUrlInvalid => !string.IsNullOrWhiteSpace(Url) && !IsWebLink(Url);

    public bool HasItems => Items.Count > 0;

    public DownloadsPageViewModel(
        DownloadsContext downloads,
        IDownloadsCoordinator coordinator,
        IFilesService filesService,
        MediaViewModelFactory mediaFactory,
        ILogger<DownloadsPageViewModel> logger)
    {
        Downloads = downloads;
        _coordinator = coordinator;
        _filesService = filesService;
        _mediaFactory = mediaFactory;
        _logger = logger;

        Items.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasItems));
        Downloads.PropertyChanged += DownloadsOnPropertyChanged;
    }

    /// <summary>
    /// Starts mirroring the queue. Called when the page is shown.
    /// </summary>
    public Task OnNavigatedToAsync() => _coordinator.StartAsync();

    private void DownloadsOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DownloadsContext.FinishedCount))
        {
            ClearFinishedCommand.NotifyCanExecuteChanged();
        }
        else if (e.PropertyName == nameof(DownloadsContext.IsUpdatingEngine))
        {
            UpdateEngineCommand.NotifyCanExecuteChanged();
        }
    }

    [RelayCommand(CanExecute = nameof(CanAdd))]
    private async Task AddAsync()
    {
        string url = Url.Trim();
        DownloadQuality quality = Qualities[Math.Clamp(SelectedQualityIndex, 0, Qualities.Length - 1)];
        Url = string.Empty;

        try
        {
            await _coordinator.AddAsync(url, quality);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Could not queue {Url}", url);
            Url = url;
        }
    }

    private bool CanAdd() => Downloads.IsSupported && IsWebLink(Url);

    [RelayCommand]
    private Task CancelAsync(DownloadItemViewModel item) => _coordinator.CancelAsync(item.Id);

    [RelayCommand]
    private Task RetryAsync(DownloadItemViewModel item) => _coordinator.RetryAsync(item.Id);

    [RelayCommand]
    private Task RemoveAsync(DownloadItemViewModel item) => _coordinator.RemoveAsync(item.Id);

    [RelayCommand(CanExecute = nameof(CanClearFinished))]
    private Task ClearFinishedAsync() => _coordinator.ClearFinishedAsync();

    private bool CanClearFinished() => Downloads.FinishedCount > 0;

    [RelayCommand(CanExecute = nameof(CanUpdateEngine))]
    private Task UpdateEngineAsync() => _coordinator.UpdateEngineAsync();

    private bool CanUpdateEngine() => Downloads.IsSupported && !Downloads.IsUpdatingEngine;

    [RelayCommand]
    private async Task PlayAsync(DownloadItemViewModel item)
    {
        StorageFile? file = await GetFileAsync(item);
        if (file is null) return;

        Messenger.Send(new PlayMediaMessage(_mediaFactory.Create(file)));
    }

    [RelayCommand]
    private async Task OpenFileLocationAsync(DownloadItemViewModel item)
    {
        StorageFile? file = await GetFileAsync(item);
        if (file is not null)
        {
            await _filesService.OpenFileLocationAsync(file);
        }
        else if (item.FilePath is not null)
        {
            await _filesService.OpenFileLocationAsync(item.FilePath);
        }
    }

    private async Task<StorageFile?> GetFileAsync(DownloadItemViewModel item)
    {
        if (item.FilePath is null) return null;

        StorageFile? file = await FilesHelpers.TryGetFileFromPathAsync(item.FilePath);
        if (file is null)
        {
            // Moved or deleted outside the app since it was downloaded.
            Messenger.Send(new NotificationMessage(NotificationLevel.Error, NotificationKind.FileOpenFailed, message: item.FilePath));
        }

        return file;
    }

    /// <summary>
    /// Accepts http(s) links only. yt-dlp decides later whether the site is supported.
    /// </summary>
    public static bool IsWebLink(string? text) =>
        Uri.TryCreate(text?.Trim(), UriKind.Absolute, out Uri? uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);
}
