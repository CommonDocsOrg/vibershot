$ErrorActionPreference = 'Stop'
# MP4 recording needs ffmpeg; fetch the LGPL build once (see tools\get-ffmpeg.ps1).
if (-not (Test-Path (Join-Path $PSScriptRoot 'tools\ffmpeg\ffmpeg.exe'))) {
    & (Join-Path $PSScriptRoot 'tools\get-ffmpeg.ps1')
}
$project = Join-Path $PSScriptRoot 'Vibershot.UI\Vibershot.UI.csproj'
dotnet build $project -c Release
if ($LASTEXITCODE -ne 0) { throw 'Vibershot build failed.' }
Start-Process -FilePath (Join-Path $PSScriptRoot 'Vibershot.UI\bin\Release\net10.0-windows\Vibershot.exe') -WindowStyle Normal
