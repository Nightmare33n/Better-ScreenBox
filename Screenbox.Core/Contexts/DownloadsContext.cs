using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Screenbox.Core.ViewModels;

namespace Screenbox.Core.Contexts;

/// <summary>
/// Observable state of the download queue, mirrored from the helper by <see cref="Coordinators.IDownloadsCoordinator"/>.
/// </summary>
public sealed partial class DownloadsContext : ObservableObject
{
    /// <summary>Gets the jobs in queue order.</summary>
    public ObservableCollection<DownloadItemViewModel> Items { get; } = new();

    [ObservableProperty]
    public partial bool IsSupported { get; set; }

    [ObservableProperty]
    public partial string? EngineVersion { get; set; }

    [ObservableProperty]
    public partial string? EngineError { get; set; }

    [ObservableProperty]
    public partial bool IsUpdatingEngine { get; set; }

    /// <summary>Gets or sets whether Windows refused to start the download helper the last time it was needed.</summary>
    [ObservableProperty]
    public partial bool HelperLaunchFailed { get; set; }

    /// <summary>Gets or sets the number of jobs that are waiting or running.</summary>
    [ObservableProperty]
    public partial int PendingCount { get; set; }

    /// <summary>Gets or sets the number of jobs that completed, failed or were canceled.</summary>
    [ObservableProperty]
    public partial int FinishedCount { get; set; }
}
