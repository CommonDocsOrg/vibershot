# Downloads a BtbN FFmpeg build (ffmpeg.exe, ffprobe.exe, LICENSE) into tools\ffmpeg.
# The binaries are never committed; run.ps1 calls this when ffmpeg.exe is missing.
# Prefers the LGPL build; falls back to GPL when the LGPL asset is not published.
[CmdletBinding()]
param(
    [string]$Destination = (Join-Path $PSScriptRoot 'ffmpeg'),
    [switch]$Force
)
$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$exe = Join-Path $Destination 'ffmpeg.exe'
if ((Test-Path $exe) -and -not $Force) {
    Write-Host "ffmpeg already present at $exe (use -Force to re-download)."
    return
}

$base = 'https://github.com/BtbN/FFmpeg-Builds/releases/download/latest'
$candidates = @(
    @{ Name = 'ffmpeg-master-latest-win64-lgpl.zip'; License = 'LGPL' },
    @{ Name = 'ffmpeg-master-latest-win64-gpl.zip';  License = 'GPL' }
)

$temp = Join-Path ([IO.Path]::GetTempPath()) ("vibershot-ffmpeg-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temp | Out-Null
try {
    $zip = $null; $picked = $null
    foreach ($candidate in $candidates) {
        $url = "$base/$($candidate.Name)"
        $target = Join-Path $temp $candidate.Name
        Write-Host "Downloading $url"
        try {
            Invoke-WebRequest -Uri $url -OutFile $target -UseBasicParsing
            $zip = $target; $picked = $candidate
            break
        } catch {
            Write-Warning "Could not download $($candidate.Name): $($_.Exception.Message)"
        }
    }
    if (-not $zip) { throw 'No FFmpeg build could be downloaded from BtbN/FFmpeg-Builds.' }

    $extract = Join-Path $temp 'extract'
    Expand-Archive -Path $zip -DestinationPath $extract -Force
    $ffmpeg = Get-ChildItem -Path $extract -Recurse -Filter 'ffmpeg.exe' | Select-Object -First 1
    $ffprobe = Get-ChildItem -Path $extract -Recurse -Filter 'ffprobe.exe' | Select-Object -First 1
    if (-not $ffmpeg -or -not $ffprobe) { throw 'The archive did not contain ffmpeg.exe and ffprobe.exe.' }
    $root = $ffmpeg.Directory.Parent.FullName
    $license = Get-ChildItem -Path $root -Filter 'LICENSE*' | Select-Object -First 1

    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    Copy-Item $ffmpeg.FullName (Join-Path $Destination 'ffmpeg.exe') -Force
    Copy-Item $ffprobe.FullName (Join-Path $Destination 'ffprobe.exe') -Force
    if ($license) { Copy-Item $license.FullName (Join-Path $Destination 'LICENSE') -Force }
    Set-Content -Path (Join-Path $Destination 'SOURCE.txt') -Value @(
        "Build: $($picked.Name) ($($picked.License))",
        "From:  $base/$($picked.Name)",
        "Date:  $(Get-Date -Format 'yyyy-MM-dd')"
    )
    $version = (& (Join-Path $Destination 'ffmpeg.exe') -version 2>&1 | Select-Object -First 1)
    Write-Host "Installed $($picked.License) build to $Destination"
    Write-Host $version
} finally {
    Remove-Item -Recurse -Force $temp -ErrorAction SilentlyContinue
}
