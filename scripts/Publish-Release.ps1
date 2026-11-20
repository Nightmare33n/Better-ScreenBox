<#
.SYNOPSIS
    Builds a signed sideload package of this fork and publishes it as a GitHub release.

.DESCRIPTION
    The app checks the latest GitHub release of Nightmare33n/Better-ScreenBox for updates
    (see AppUpdateService). This script produces what that check expects:

    1. Creates a self-signed code-signing certificate the first time (screenbox-release.pfx
       plus screenbox-release.password at the repository root, both git-ignored) and reuses
       it afterwards. Every release must be signed with the same certificate, or Windows
       refuses to install it over the previous version.
    2. Temporarily gives the manifest the fork identity (Nightmare33n.BetterScreenbox) and
       the requested version, builds a Release .msixbundle, and restores the manifest.
    3. Collects the bundle, the public certificate and a zipped install kit (with
       Install.ps1, which trusts the certificate and installs the dependencies).
    4. Creates the GitHub release with those files unless -SkipUpload is given.

.PARAMETER Version
    The release version as Major.Minor.Build, for example 1.0.0. The package version
    becomes Major.Minor.Build.0 and the tag v<Version>.

.PARAMETER Notes
    The release notes (Markdown).

.PARAMETER Platforms
    The processor architectures to include in the bundle. Default: x64.

.PARAMETER SkipUpload
    Builds the files into artifacts/ without creating a GitHub release.

.EXAMPLE
    PS> ./scripts/Publish-Release.ps1 -Version 1.0.0 -Notes "First release with downloads"
#>

[CmdletBinding()]
param (
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version,

    [Parameter(Mandatory = $true)]
    [string]$Notes,

    [ValidateSet('x64', 'x86', 'arm64')]
    [string[]]$Platforms = @('x64'),

    [switch]$SkipUpload
)

$ErrorActionPreference = 'Stop'

New-Variable -Name IdentityName -Value 'Nightmare33n.BetterScreenbox' -Option Constant
New-Variable -Name Publisher -Value 'CN=Nightmare33n' -Option Constant
New-Variable -Name PublisherDisplayName -Value 'Nightmare33n' -Option Constant
New-Variable -Name DisplayName -Value 'Better Screenbox' -Option Constant
New-Variable -Name Repository -Value 'Nightmare33n/Better-ScreenBox' -Option Constant

$repositoryPath = Split-Path -Parent $PSScriptRoot
$manifestPath = Join-Path $repositoryPath 'Screenbox/Package.appxmanifest'
$projectPath = Join-Path $repositoryPath 'Screenbox/Screenbox.csproj'
$pfxPath = Join-Path $repositoryPath 'screenbox-release.pfx'
$passwordPath = Join-Path $repositoryPath 'screenbox-release.password'
$artifactsPath = Join-Path $repositoryPath "artifacts/v$Version"
$packageOutputPath = Join-Path $artifactsPath 'packages'
$packageVersion = "$Version.0"
$tag = "v$Version"

function Get-MSBuild {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    $path = & $vswhere -version '[18.0,19.0)' -latest -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
    if (-not $path) { $path = 'C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe' }
    if (-not (Test-Path $path)) { throw 'MSBuild from Visual Studio 2026 was not found.' }
    return $path
}

function Get-SigningCertificate {
    if ((Test-Path $pfxPath) -and (Test-Path $passwordPath)) {
        return Get-Content -Path $passwordPath -Raw
    }

    Write-Host 'Creating the release signing certificate (first release only)'
    $password = [Convert]::ToBase64String([System.Security.Cryptography.RandomNumberGenerator]::GetBytes(24))
    $certificate = New-SelfSignedCertificate `
        -Type Custom `
        -Subject $Publisher `
        -FriendlyName "$DisplayName release signing" `
        -KeyUsage DigitalSignature `
        -KeyAlgorithm RSA -KeyLength 3072 `
        -NotAfter (Get-Date).AddYears(10) `
        -CertStoreLocation 'Cert:\CurrentUser\My' `
        -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}')
    $securePassword = ConvertTo-SecureString -String $password -AsPlainText -Force
    Export-PfxCertificate -Cert $certificate -FilePath $pfxPath -Password $securePassword | Out-Null
    Set-Content -Path $passwordPath -Value $password -NoNewline
    Remove-Item -Path "Cert:\CurrentUser\My\$($certificate.Thumbprint)" -Force
    return $password
}

function Set-ReleaseIdentity([xml]$Manifest) {
    $Manifest.Package.Identity.Name = $IdentityName
    $Manifest.Package.Identity.Publisher = $Publisher
    $Manifest.Package.Identity.Version = $packageVersion
    $Manifest.Package.Properties.DisplayName = $DisplayName
    $Manifest.Package.Properties.PublisherDisplayName = $PublisherDisplayName
    $Manifest.Package.Applications.Application.VisualElements.DisplayName = $DisplayName
}

$git = git -C $repositoryPath status --porcelain --untracked-files=no
if ($git -and -not $SkipUpload) {
    throw 'Commit your changes first: the release tag points at the current commit.'
}

$password = Get-SigningCertificate
$msbuild = Get-MSBuild
$originalManifestBytes = [System.IO.File]::ReadAllBytes($manifestPath)
$originalManifest = Get-Content -Path $manifestPath -Raw

Remove-Item -Path $artifactsPath -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $packageOutputPath | Out-Null

try {
    [xml]$manifest = $originalManifest
    Set-ReleaseIdentity $manifest
    $settings = New-Object System.Xml.XmlWriterSettings
    $settings.Encoding = [System.Text.UTF8Encoding]::new($false)
    $settings.Indent = $true
    $writer = [System.Xml.XmlWriter]::Create($manifestPath, $settings)
    try { $manifest.Save($writer) } finally { $writer.Dispose() }

    Write-Host "Building $DisplayName $packageVersion for $($Platforms -join ', ')"
    & $msbuild $projectPath `
        -restore `
        -nologo -v:m `
        -p:Configuration=Release `
        -p:Platform=$($Platforms[0]) `
        -p:AppxBundle=Always `
        -p:AppxBundlePlatforms=$($Platforms -join '|') `
        -p:UapAppxPackageBuildMode=SideloadOnly `
        -p:ContinuousIntegrationBuild=true `
        -p:GenerateTestArtifacts=true `
        -p:AppxPackageSigningEnabled=true `
        -p:PackageCertificateKeyFile=$pfxPath `
        -p:PackageCertificatePassword=$password `
        -p:AppxPackageDir="$packageOutputPath\"
    if ($LASTEXITCODE -ne 0) { throw "MSBuild failed with exit code $LASTEXITCODE." }
}
finally {
    # Development builds keep the original identity, so a debug deploy never replaces the release install.
    [System.IO.File]::WriteAllBytes($manifestPath, $originalManifestBytes)
}

$testFolder = Get-ChildItem -Path $packageOutputPath -Directory -Filter '*_Test' | Select-Object -First 1
if (-not $testFolder) { throw "No package folder was produced in $packageOutputPath." }

$bundle = Get-ChildItem -Path $testFolder.FullName -File -Filter '*.msixbundle' | Select-Object -First 1
$certificate = Get-ChildItem -Path $testFolder.FullName -File -Filter '*.cer' | Select-Object -First 1
if (-not $bundle) { throw 'The build produced no .msixbundle.' }

$bundleName = "BetterScreenbox_$($packageVersion)_$($Platforms -join '_').msixbundle"
$certificateName = 'BetterScreenbox.cer'
$kitName = "BetterScreenbox_$($packageVersion)_Install.zip"
Copy-Item -Path $bundle.FullName -Destination (Join-Path $artifactsPath $bundleName)
if ($certificate) {
    Copy-Item -Path $certificate.FullName -Destination (Join-Path $artifactsPath $certificateName)
}
else {
    # MSBuild only copies the .cer next to the bundle for some signing setups; export it ourselves otherwise.
    $pfx = Get-PfxData -FilePath $pfxPath -Password (ConvertTo-SecureString $password -AsPlainText -Force)
    Export-Certificate -Cert $pfx.EndEntityCertificates[0] -FilePath (Join-Path $artifactsPath $certificateName) | Out-Null
    Copy-Item -Path (Join-Path $artifactsPath $certificateName) -Destination $testFolder.FullName
}
# The kit is what Install.ps1 needs: the bundle, the certificate and the dependencies. The loose .msix duplicates
# the bundle and the .appxsym symbols are only for crash analysis, so both stay out to halve the download.
$kitItems = Get-ChildItem -Path $testFolder.FullName | Where-Object { $_.Extension -notin '.msix', '.appxsym' }
Compress-Archive -Path $kitItems.FullName -DestinationPath (Join-Path $artifactsPath $kitName) -Force

$files = @($bundleName, $certificateName, $kitName) | ForEach-Object { Join-Path $artifactsPath $_ }
Write-Host 'Release files:'
$files | ForEach-Object { Write-Host "  $_ ($([math]::Round((Get-Item $_).Length / 1MB, 1)) MB)" }

if ($SkipUpload) { return }

$notesPath = Join-Path $artifactsPath 'notes.md'
Set-Content -Path $notesPath -Value $Notes -Encoding utf8
$commit = git -C $repositoryPath rev-parse HEAD
gh release create $tag @files --repo $Repository --target $commit --title "$DisplayName $Version" --notes-file $notesPath
if ($LASTEXITCODE -ne 0) { throw "gh release create failed with exit code $LASTEXITCODE." }
