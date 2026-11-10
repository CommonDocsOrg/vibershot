$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'Vibershot.UI\Vibershot.UI.csproj'
dotnet build $project -c Release
if ($LASTEXITCODE -ne 0) { throw 'Vibershot build failed.' }
Start-Process -FilePath (Join-Path $PSScriptRoot 'Vibershot.UI\bin\Release\net10.0-windows\Vibershot.exe') -WindowStyle Normal
