using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Screenbox.Core.Downloads.Contracts;
using Screenbox.Core.Helpers;
using Windows.ApplicationModel;
using Windows.Foundation.Metadata;
using Windows.Storage;

namespace Screenbox.Core.Services;

/// <inheritdoc cref="IDownloaderService"/>
/// <remarks>
/// The app never writes the queue file. Every change goes through a command file so the helper stays its single
/// writer; this keeps the two processes from overwriting each other's updates.
/// </remarks>
public sealed class DownloaderService : IDownloaderService
{
    private const string FullTrustContract = "Windows.ApplicationModel.FullTrustAppContract";

    private readonly ILogger<DownloaderService> _logger;
    private string? _rootPath;

    public DownloaderService(ILogger<DownloaderService> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc/>
    public bool IsSupported { get; } = ComputeIsSupported();

    private string RootPath => _rootPath ??= Path.Combine(ApplicationData.Current.LocalFolder.Path, DownloaderPaths.RootFolderName);

    /// <inheritdoc/>
    public async Task<DownloaderSnapshot?> ReadSnapshotAsync()
    {
        string path = Path.Combine(RootPath, DownloaderPaths.SnapshotFileName);
        if (!File.Exists(path)) return new DownloaderSnapshot();

        try
        {
            string json = await File.ReadAllTextAsync(path);
            return JsonSerializer.Deserialize(json, DownloaderJsonContext.Default.DownloaderSnapshot);
        }
        catch (IOException)
        {
            // The helper is swapping the file in; the next tick will see the new one.
            return null;
        }
        catch (JsonException e)
        {
            _logger.LogWarning(e, "The download queue file could not be read");
            return null;
        }
    }

    /// <inheritdoc/>
    public async Task SendCommandAsync(DownloaderCommand command)
    {
        string folder = Path.Combine(RootPath, DownloaderPaths.CommandsFolderName);
        Directory.CreateDirectory(folder);

        // Ticks first so the helper can apply commands in the order they were sent.
        string name = $"{DateTime.UtcNow.Ticks:D20}-{Guid.NewGuid():N}";
        string temporaryPath = Path.Combine(folder, name + ".tmp");
        string json = JsonSerializer.Serialize(command, DownloaderJsonContext.Default.DownloaderCommand);
        await File.WriteAllTextAsync(temporaryPath, json);

        // The helper only reads *.json, so it never sees a half-written command.
        File.Move(temporaryPath, Path.Combine(folder, name + ".json"));
    }

    /// <inheritdoc/>
    public async Task<bool> LaunchHelperAsync()
    {
        if (!IsSupported) return false;

        try
        {
            await LaunchFullTrustProcessAsync();
            return true;
        }
        catch (Exception e)
        {
            _logger.LogError(e, "The download helper could not be started");
            return false;
        }
    }

    private static Task LaunchFullTrustProcessAsync() =>
        FullTrustProcessLauncher.LaunchFullTrustProcessForCurrentAppAsync().AsTask();

    private static bool ComputeIsSupported()
    {
        try
        {
            return SystemInformation.IsDesktop && ApiInformation.IsApiContractPresent(FullTrustContract, 1);
        }
        catch (Exception)
        {
            return false;
        }
    }
}
