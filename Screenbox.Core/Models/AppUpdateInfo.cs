using System;

namespace Screenbox.Core.Models;

/// <summary>
/// A newer release found on GitHub.
/// </summary>
/// <param name="Version">The package version of the release.</param>
/// <param name="TagName">The release tag, for display.</param>
/// <param name="ReleaseNotes">The release body, in Markdown.</param>
/// <param name="PackageUri">The download link of the app package.</param>
/// <param name="PackageFileName">The file name of the app package.</param>
/// <param name="PackageSize">The package size in bytes.</param>
/// <param name="ReleasePageUri">The release page on GitHub.</param>
public sealed record AppUpdateInfo(
    Version Version,
    string TagName,
    string? ReleaseNotes,
    Uri PackageUri,
    string PackageFileName,
    long PackageSize,
    Uri ReleasePageUri);
