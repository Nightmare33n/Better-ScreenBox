using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Screenbox.Core.Models;
using Windows.ApplicationModel;
using Windows.Storage;
using Windows.System;

namespace Screenbox.Core.Services;

/// <inheritdoc cref="IAppUpdateService"/>
/// <remarks>
/// Releases are plain GitHub releases with a signed .msixbundle attached. The repository is public, so the API
/// needs no credentials. The JSON is read with <see cref="JsonDocument"/>, which needs no reflection under AOT.
/// </remarks>
public sealed class AppUpdateService : IAppUpdateService
{
    /// <summary>The repository whose releases carry the app packages.</summary>
    public const string ReleasesRepository = "Nightmare33n/Better-ScreenBox";

    /// <summary>The package identity name used by release builds (see scripts/Publish-Release.ps1).</summary>
    public const string ReleasePackageName = "Nightmare33n.BetterScreenbox";

    private static readonly HttpClient Http = CreateClient();

    /// <inheritdoc/>
    public Version CurrentVersion { get; }

    /// <inheritdoc/>
    public bool IsReleaseBuild { get; }

    public AppUpdateService()
    {
        PackageId id = Package.Current.Id;
        CurrentVersion = new Version(id.Version.Major, id.Version.Minor, id.Version.Build, id.Version.Revision);
        IsReleaseBuild = id.Name == ReleasePackageName;
    }

    /// <inheritdoc/>
    public async Task<AppUpdateInfo?> CheckForUpdateAsync(CancellationToken cancellationToken = default)
    {
        using HttpResponseMessage response = await Http.GetAsync($"https://api.github.com/repos/{ReleasesRepository}/releases/latest", cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();

        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using JsonDocument document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        AppUpdateInfo? release = ParseRelease(document.RootElement);
        return release is not null && release.Version > CurrentVersion ? release : null;
    }

    /// <inheritdoc/>
    public async Task<StorageFile> DownloadAsync(AppUpdateInfo update, IProgress<double>? progress, CancellationToken cancellationToken = default)
    {
        StorageFile file = await ApplicationData.Current.TemporaryFolder.CreateFileAsync(update.PackageFileName, CreationCollisionOption.ReplaceExisting);

        using HttpResponseMessage response = await Http.GetAsync(update.PackageUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        long total = response.Content.Headers.ContentLength ?? update.PackageSize;

        await using Stream source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using Stream target = await file.OpenStreamForWriteAsync();
        target.SetLength(0);

        byte[] buffer = new byte[81920];
        long received = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
        {
            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            received += read;
            if (total > 0) progress?.Report((double)received / total);
        }

        return file;
    }

    /// <inheritdoc/>
    public async Task<bool> LaunchInstallerAsync(StorageFile package)
    {
        return await Launcher.LaunchFileAsync(package);
    }

    /// <summary>
    /// Reads a GitHub release object. Returns <see langword="null"/> for drafts, pre-releases, unparsable tags or
    /// releases without an app package.
    /// </summary>
    public static AppUpdateInfo? ParseRelease(JsonElement release)
    {
        if (GetBool(release, "draft") || GetBool(release, "prerelease")) return null;

        string? tag = GetString(release, "tag_name");
        if (tag is null || !TryParseVersion(tag, out Version? version)) return null;

        if (!release.TryGetProperty("assets", out JsonElement assets) || assets.ValueKind != JsonValueKind.Array) return null;

        JsonElement? package = null;
        foreach (JsonElement asset in assets.EnumerateArray())
        {
            string name = GetString(asset, "name") ?? string.Empty;
            if (name.EndsWith(".msixbundle", StringComparison.OrdinalIgnoreCase))
            {
                package = asset;
                break;
            }

            if (name.EndsWith(".msix", StringComparison.OrdinalIgnoreCase))
            {
                package ??= asset;
            }
        }

        if (package is not JsonElement chosen) return null;

        string? downloadUrl = GetString(chosen, "browser_download_url");
        string? pageUrl = GetString(release, "html_url");
        if (!Uri.TryCreate(downloadUrl, UriKind.Absolute, out Uri? packageUri) || !Uri.TryCreate(pageUrl, UriKind.Absolute, out Uri? pageUri)) return null;

        long size = chosen.TryGetProperty("size", out JsonElement sizeElement) && sizeElement.TryGetInt64(out long value) ? value : 0;
        return new AppUpdateInfo(version, tag, GetString(release, "body"), packageUri, GetString(chosen, "name")!, size, pageUri);
    }

    /// <summary>
    /// Parses tags like <c>v1.2.3</c> or <c>1.2.3.4</c> into a four-part package version.
    /// </summary>
    public static bool TryParseVersion(string tag, [NotNullWhen(true)] out Version? version)
    {
        string text = tag.Trim().TrimStart('v', 'V');
        int suffix = text.IndexOfAny(['-', '+', ' ']);
        if (suffix >= 0) text = text[..suffix];

        if (Version.TryParse(text, out Version? parsed))
        {
            version = new Version(parsed.Major, parsed.Minor, Math.Max(parsed.Build, 0), Math.Max(parsed.Revision, 0));
            return true;
        }

        version = null;
        return false;
    }

    private static HttpClient CreateClient()
    {
        HttpClient client = new() { Timeout = TimeSpan.FromMinutes(10) };
        // GitHub rejects API calls without a User-Agent.
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Screenbox-Updater", "1.0"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool GetBool(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.True;
}
