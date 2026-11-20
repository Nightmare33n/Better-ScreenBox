<#
.SYNOPSIS
    Downloads the third-party executables bundled with the Screenbox downloader helper.

.DESCRIPTION
    The download feature runs yt-dlp from a packaged full-trust helper. yt-dlp needs
    ffmpeg/ffprobe to merge separate video and audio streams, and a JavaScript runtime
    (Deno) to solve YouTube's player challenges. None of these binaries are committed to
    the repository: this script fetches them into Screenbox.Downloader/tools, which the
    app project then packs into the MSIX under the Downloader folder.

    The script is idempotent. Files that already exist are kept unless -Force is given.

.PARAMETER Force
    Re-downloads every tool even if it already exists.

.EXAMPLE
    PS> ./scripts/Get-DownloaderTools.ps1
#>

[CmdletBinding()]
param (
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$repositoryPath = Split-Path -Parent $PSScriptRoot
$toolsPath = Join-Path $repositoryPath 'Screenbox.Downloader/tools'
$tempPath = Join-Path ([System.IO.Path]::GetTempPath()) "screenbox-tools-$([guid]::NewGuid().ToString('N'))"

New-Item -ItemType Directory -Force -Path $toolsPath | Out-Null
New-Item -ItemType Directory -Force -Path $tempPath | Out-Null

function Test-Tool([string]$Name) {
    -not $Force -and (Test-Path (Join-Path $toolsPath $Name))
}

function Save-Url([string]$Url, [string]$Destination) {
    Write-Host "Downloading $Url"
    Invoke-WebRequest -Uri $Url -OutFile $Destination -UseBasicParsing
}

try {
    if (-not (Test-Tool 'yt-dlp.exe')) {
        Save-Url 'https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe' (Join-Path $toolsPath 'yt-dlp.exe')
    }

    if (-not (Test-Tool 'ffmpeg.exe')) {
        # The shared build keeps ffmpeg and ffprobe small by sharing the codec DLLs.
        $zip = Join-Path $tempPath 'ffmpeg.zip'
        Save-Url 'https://github.com/yt-dlp/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl-shared.zip' $zip
        Expand-Archive -Path $zip -DestinationPath $tempPath -Force
        $bin = Get-ChildItem -Path $tempPath -Directory -Filter 'ffmpeg-*' | Select-Object -First 1 | ForEach-Object { Join-Path $_.FullName 'bin' }
        Get-ChildItem -Path $bin -File | Where-Object { $_.Name -ne 'ffplay.exe' } | Copy-Item -Destination $toolsPath -Force
    }

    if (-not (Test-Tool 'deno.exe')) {
        $zip = Join-Path $tempPath 'deno.zip'
        Save-Url 'https://github.com/denoland/deno/releases/latest/download/deno-x86_64-pc-windows-msvc.zip' $zip
        Expand-Archive -Path $zip -DestinationPath (Join-Path $tempPath 'deno') -Force
        Copy-Item -Path (Join-Path $tempPath 'deno/deno.exe') -Destination $toolsPath -Force
    }

    Get-ChildItem -Path $toolsPath -File | Sort-Object Name | Format-Table Name, @{ Name = 'MB'; Expression = { [math]::Round($_.Length / 1MB, 1) } } -AutoSize
}
finally {
    Remove-Item -Path $tempPath -Recurse -Force -ErrorAction SilentlyContinue
}
