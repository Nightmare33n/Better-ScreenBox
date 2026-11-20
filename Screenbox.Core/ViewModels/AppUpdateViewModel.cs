using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Screenbox.Core.Contexts;
using Screenbox.Core.Models;
using Screenbox.Core.Services;
using Windows.Storage;

namespace Screenbox.Core.ViewModels;

/// <summary>
/// The state of the last update check, so the view can pick the matching text.
/// </summary>
public enum AppUpdateStatus
{
    Idle,
    Checking,
    UpToDate,
    Available,
    Downloading,
    Installing,
    Failed,
}

/// <summary>
/// Shared update state for the shell banner and the Settings page. Registered as a singleton so both show the
/// same check and the same download.
/// </summary>
public sealed partial class AppUpdateViewModel : ObservableObject
{
    private readonly IAppUpdateService _updateService;
    private readonly GeneralSettingsContext _settings;
    private readonly ILogger<AppUpdateViewModel> _logger;
    private bool _startupCheckDone;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsUpdateAvailable), nameof(AvailableVersionText), nameof(IsBusy))]
    [NotifyCanExecuteChangedFor(nameof(CheckForUpdatesCommand), nameof(InstallUpdateCommand))]
    public partial AppUpdateStatus Status { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsUpdateAvailable), nameof(AvailableVersionText))]
    public partial AppUpdateInfo? AvailableUpdate { get; set; }

    /// <summary>Gets or sets the download progress from 0 to 100.</summary>
    [ObservableProperty]
    public partial double DownloadProgress { get; set; }

    /// <summary>Gets or sets whether the user closed the banner for this session.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBannerOpen))]
    public partial bool IsBannerDismissed { get; set; }

    public string CurrentVersionText { get; }

    public bool IsUpdateAvailable => AvailableUpdate is not null && Status is AppUpdateStatus.Available or AppUpdateStatus.Downloading or AppUpdateStatus.Installing or AppUpdateStatus.Failed;

    public bool IsBannerOpen => IsUpdateAvailable && !IsBannerDismissed;

    public bool IsBusy => Status is AppUpdateStatus.Checking or AppUpdateStatus.Downloading or AppUpdateStatus.Installing;

    public string AvailableVersionText => AvailableUpdate?.Version.ToString(3) ?? string.Empty;

    public bool CheckAutomatically
    {
        get => _settings.CheckForUpdates;
        set
        {
            if (_settings.CheckForUpdates == value) return;
            _settings.CheckForUpdates = value;
            OnPropertyChanged();
        }
    }

    public AppUpdateViewModel(IAppUpdateService updateService, GeneralSettingsContext settings, ILogger<AppUpdateViewModel> logger)
    {
        _updateService = updateService;
        _settings = settings;
        _logger = logger;
        CurrentVersionText = updateService.CurrentVersion.ToString(3);
    }

    partial void OnStatusChanged(AppUpdateStatus value) => OnPropertyChanged(nameof(IsBannerOpen));

    partial void OnAvailableUpdateChanged(AppUpdateInfo? value) => OnPropertyChanged(nameof(IsBannerOpen));

    /// <summary>
    /// Checks once per app launch when automatic checks are on and this is a release install.
    /// </summary>
    public async Task CheckOnStartupAsync()
    {
        if (_startupCheckDone || !CheckAutomatically || !_updateService.IsReleaseBuild) return;
        _startupCheckDone = true;
        await CheckAsync(silent: true);
    }

    [RelayCommand(CanExecute = nameof(CanCheck))]
    private Task CheckForUpdatesAsync() => CheckAsync(silent: false);

    private bool CanCheck() => !IsBusy;

    private async Task CheckAsync(bool silent)
    {
        Status = AppUpdateStatus.Checking;
        try
        {
            AvailableUpdate = await _updateService.CheckForUpdateAsync();
            Status = AvailableUpdate is null ? AppUpdateStatus.UpToDate : AppUpdateStatus.Available;
        }
        catch (Exception e)
        {
            // An offline startup check should not turn into an error banner.
            _logger.LogWarning(e, "Update check failed");
            Status = silent ? AppUpdateStatus.Idle : AppUpdateStatus.Failed;
        }
    }

    [RelayCommand(CanExecute = nameof(CanInstall))]
    private async Task InstallUpdateAsync()
    {
        if (AvailableUpdate is not AppUpdateInfo update) return;

        Status = AppUpdateStatus.Downloading;
        DownloadProgress = 0;
        try
        {
            Progress<double> progress = new(value => DownloadProgress = value * 100);
            StorageFile package = await _updateService.DownloadAsync(update, progress);
            Status = AppUpdateStatus.Installing;
            bool launched = await _updateService.LaunchInstallerAsync(package);
            Status = launched ? AppUpdateStatus.Installing : AppUpdateStatus.Failed;
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Update download failed");
            Status = AppUpdateStatus.Failed;
        }
    }

    private bool CanInstall() => AvailableUpdate is not null && !IsBusy;

    [RelayCommand]
    private void DismissBanner() => IsBannerDismissed = true;
}
