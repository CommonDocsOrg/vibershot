<#
.SYNOPSIS
Publishes a built installer as a GitHub release so installed copies see the update.

.DESCRIPTION
Takes the VibershotSetup-<version>.exe and latest.json that tools\build-installer.ps1 produced,
tags the commit v<version>, and creates the GitHub release with both files attached using the gh
CLI. The release is created as a draft unless -Publish is given, so the assets can be checked
first; installed copies only pick up a release once it is published and marked latest.
No GitHub Actions involved: everything runs on this machine.

.PARAMETER Version
The version that was built. Defaults to the version in latest.json.

.PARAMETER Publish
Publish immediately instead of leaving a draft.

.PARAMETER PreRelease
Mark the release as a pre-release. GitHub's "latest" feed skips pre-releases, so installed copies
will not be offered it; use for testing a build with a hand-edited FeedUrl.

.EXAMPLE
.\tools\publish-release.ps1            # draft release for review
.\tools\publish-release.ps1 -Publish   # live: every installed copy is offered it within a day
#>
[CmdletBinding()]
param(
    [string]$Version,
    [switch]$Publish,
    [switch]$PreRelease,
    [string]$NotesFile
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$outputDir = Join-Path $root 'artifacts\installer'
$manifestPath = Join-Path $outputDir 'latest.json'

if (-not (Get-Command gh -ErrorAction SilentlyContinue)) { throw 'The GitHub CLI (gh) is not installed: winget install --id GitHub.cli -e, then gh auth login.' }
if (-not (Test-Path $manifestPath)) { throw "No $manifestPath. Run tools\build-installer.ps1 first." }
$manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json
if (-not $Version) { $Version = $manifest.version }
$Version = $Version.TrimStart('v')
if ($manifest.version -ne $Version) { throw "latest.json is for $($manifest.version), not $Version. Rebuild with -Version $Version." }
$setup = Join-Path $outputDir "VibershotSetup-$Version.exe"
if (-not (Test-Path $setup)) { throw "Missing $setup." }
$hash = (Get-FileHash $setup -Algorithm SHA256).Hash.ToLowerInvariant()
if ($hash -ne $manifest.sha256) { throw 'The installer no longer matches latest.json. Rebuild before publishing.' }

$tag = "v$Version"
Push-Location $root
try {
    if (git status --porcelain) { Write-Warning 'The working tree has uncommitted changes. The release will point at the current HEAD commit.' }
    if (-not (git tag --list $tag)) {
        git tag -a $tag -m "Vibershot $Version"
        if ($LASTEXITCODE -ne 0) { throw "Could not create tag $tag." }
    }
    git push origin $tag
    if ($LASTEXITCODE -ne 0) { throw "Could not push tag $tag." }

    gh release view $tag *> $null
    $exists = $LASTEXITCODE -eq 0
    if ($exists) {
        # A draft from an earlier run: refresh the assets, then publish if asked.
        gh release upload $tag $setup $manifestPath --clobber
        if ($LASTEXITCODE -ne 0) { throw 'gh release upload failed.' }
        if ($Publish) {
            gh release edit $tag --draft=false --latest
            if ($LASTEXITCODE -ne 0) { throw 'gh release edit failed.' }
        }
    } else {
        $ghArgs = @('release', 'create', $tag, $setup, $manifestPath, '--title', "Vibershot $Version", '--verify-tag')
        if ($NotesFile) { $ghArgs += @('--notes-file', $NotesFile) }
        elseif ($manifest.notes) { $ghArgs += @('--notes', $manifest.notes) }
        else { $ghArgs += @('--generate-notes') }
        if (-not $Publish) { $ghArgs += '--draft' }
        if ($PreRelease) { $ghArgs += '--prerelease' } elseif ($Publish) { $ghArgs += '--latest' }
        gh @ghArgs
        if ($LASTEXITCODE -ne 0) { throw 'gh release create failed.' }
    }
}
finally { Pop-Location }

if ($Publish) {
    Write-Host "Published $tag. Installed copies will be offered it at their next daily check." -ForegroundColor Green
    Write-Host "Feed: https://github.com/$(($manifest.releaseUrl -split '/')[3..4] -join '/')/releases/latest/download/latest.json"
} else {
    Write-Host "Draft release $tag created. Review it on GitHub, then click Publish release (or re-run with -Publish)." -ForegroundColor Yellow
}
