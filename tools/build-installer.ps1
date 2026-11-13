<#
.SYNOPSIS
Builds the Vibershot Windows installer and the latest.json update feed next to it.

.DESCRIPTION
Publishes Vibershot.UI self-contained for win-x64 (with the bundled ffmpeg), compiles
tools\installer\Vibershot.iss with Inno Setup, then writes artifacts\installer\latest.json with the
installer's SHA-256 so the app's update check can verify what it downloads.
See docs\RELEASING.md for the full release flow.

.PARAMETER Version
The release version, "1.2.3" or "1.2.3-beta.1". Defaults to <Version> in Vibershot.UI.csproj.

.PARAMETER Notes
Release notes shown in the app's update dialog. A path to a text/markdown file or the text itself.

.PARAMETER RunChecks
Also builds and runs tests\CaptureChecks before packaging; the build stops if they fail.

.EXAMPLE
.\tools\build-installer.ps1 -Version 0.2.0 -Notes .\RELEASE_NOTES.md -RunChecks
#>
[CmdletBinding()]
param(
    [string]$Version,
    [string]$Notes = '',
    [switch]$RunChecks,
    [switch]$SkipPublish,
    [string]$Configuration = 'Release'
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent          # ...\src
$uiProject = Join-Path $root 'Vibershot.UI\Vibershot.UI.csproj'
$publishDir = Join-Path $root 'artifacts\publish'
$outputDir = Join-Path $root 'artifacts\installer'
$iss = Join-Path $PSScriptRoot 'installer\Vibershot.iss'
$repo = 'CommonDocsOrg/vibershot'

# ---- Version: the csproj is the single source of truth unless -Version overrides it.
if (-not $Version) {
    $Version = ([xml](Get-Content $uiProject)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
    if (-not $Version) { throw "No <Version> in $uiProject; pass -Version." }
}
$Version = $Version.Trim().TrimStart('v')
if ($Version -notmatch '^(\d+)\.(\d+)\.(\d+)(-[0-9A-Za-z.]+)?$') { throw "Version '$Version' must look like 1.2.3 or 1.2.3-beta.1" }
$numeric = "$($Matches[1]).$($Matches[2]).$($Matches[3]).0"
Write-Host "Building Vibershot $Version" -ForegroundColor Cyan

# ---- Inno Setup compiler.
$iscc = @(
    (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
    (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
    (Get-Command ISCC.exe -ErrorAction SilentlyContinue).Source
) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
if (-not $iscc) { throw 'Inno Setup 6 is not installed. Run: winget install --id JRSoftware.InnoSetup -e' }

# ---- A running Vibershot locks the DLLs the publish step overwrites.
if (Get-Process Vibershot -ErrorAction SilentlyContinue) { throw 'Vibershot is running. Exit it from the tray before building.' }

# ---- ffmpeg must ship inside the installer.
$ffmpeg = Join-Path $PSScriptRoot 'ffmpeg\ffmpeg.exe'
if (-not (Test-Path $ffmpeg)) { & (Join-Path $PSScriptRoot 'get-ffmpeg.ps1') }
if (-not (Test-Path (Join-Path $PSScriptRoot 'ffmpeg\LICENSE'))) { Write-Warning 'tools\ffmpeg\LICENSE is missing; the LGPL build must ship with its license.' }

# ---- Optional library checks.
if ($RunChecks) {
    $checks = Join-Path $root 'tests\CaptureChecks\CaptureChecks.csproj'
    dotnet build $checks -c $Configuration -nologo -v q
    if ($LASTEXITCODE -ne 0) { throw 'CaptureChecks failed to build.' }
    & (Join-Path $root "tests\CaptureChecks\bin\$Configuration\net10.0-windows\CaptureChecks.exe")
    if ($LASTEXITCODE -ne 0) { throw 'CaptureChecks reported a failure; not packaging.' }
}

# ---- Publish: self-contained so the installer never depends on a .NET runtime being present.
if (-not $SkipPublish) {
    if (Test-Path $publishDir) { Remove-Item -Recurse -Force $publishDir }
    dotnet publish $uiProject -c $Configuration -r win-x64 --self-contained true -o $publishDir -nologo -v q `
        -p:Version=$Version -p:PublishSingleFile=false -p:PublishReadyToRun=false -p:DebugType=none -p:SatelliteResourceLanguages=en
    if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }
}
foreach ($required in 'Vibershot.exe', 'ffmpeg\ffmpeg.exe', 'ffmpeg\LICENSE') {
    if (-not (Test-Path (Join-Path $publishDir $required))) { throw "Publish output is missing $required." }
}

# ---- Compile the installer.
New-Item -ItemType Directory -Force $outputDir | Out-Null
& $iscc /Q "/DAppVersion=$Version" "/DNumericVersion=$numeric" "/DPublishDir=$publishDir" "/DOutputDir=$outputDir" $iss
if ($LASTEXITCODE -ne 0) { throw 'Inno Setup failed.' }
$setup = Join-Path $outputDir "VibershotSetup-$Version.exe"
if (-not (Test-Path $setup)) { throw "Expected $setup after compiling." }

# ---- latest.json: what the app downloads from the release to learn about this version.
if ($Notes -and (Test-Path -LiteralPath $Notes -PathType Leaf)) { $Notes = Get-Content -LiteralPath $Notes -Raw }
$hash = (Get-FileHash $setup -Algorithm SHA256).Hash.ToLowerInvariant()
$manifest = [ordered]@{
    version      = $Version
    installerUrl = "https://github.com/$repo/releases/download/v$Version/VibershotSetup-$Version.exe"
    sha256       = $hash
    size         = (Get-Item $setup).Length
    publishedUtc = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
    releaseUrl   = "https://github.com/$repo/releases/tag/v$Version"
    notes        = $Notes.Trim()
}
$manifestPath = Join-Path $outputDir 'latest.json'
[IO.File]::WriteAllText($manifestPath, ($manifest | ConvertTo-Json -Depth 3) + "`n", [Text.UTF8Encoding]::new($false))

Write-Host ''
Write-Host "Installer : $setup ($([math]::Round((Get-Item $setup).Length / 1MB, 1)) MB)" -ForegroundColor Green
Write-Host "SHA-256   : $hash"
Write-Host "Feed      : $manifestPath"
Write-Host 'Next      : .\tools\publish-release.ps1 (or upload both files to the GitHub release by hand).'
