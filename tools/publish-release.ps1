#Requires -Version 7
<#
.SYNOPSIS
  Pushes main and publishes it as a GitHub release; players see "(Update available)" at their next start.

.DESCRIPTION
  Needs a clean main pushed to GitHub, the GitHub CLI (gh) logged in, and the release signing key. Packs a signed zip
  (make-release-zip.ps1) and creates release v0.1.<count> with it. Games look up the latest release at start and
  offer it on the title screen; players install it with Online > Check for updates.

.PARAMETER GameDir
  Sea of Stars install folder. Optional: tools\game-dir.ps1 finds it (SOS_GAME_DIR, game-dir.txt, or Steam's libraries).
  It needs BepInEx and its interop assemblies (to build).

.PARAMETER Notes
  Release notes (default: the commit subjects since the previous release).
#>
param(
    [string]$GameDir,
    [string]$Notes
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot

if ((git -C $root rev-parse --abbrev-ref HEAD).Trim() -ne "main") { throw "Publish from main." }
if (git -C $root status --porcelain) { throw "Commit or stash your changes first: releases are built from a clean main." }
if (-not (Test-Path (Join-Path $env:APPDATA "SosOnline\release-signing-key.txt"))) { throw "No release signing key: run tools\new-signing-key.ps1 once." }

git -C $root push origin main
if ($LASTEXITCODE -ne 0) { throw "Push failed." }
git -C $root fetch --tags --quiet origin

& "$PSScriptRoot\make-release-zip.ps1" -GameDir $GameDir
$count = (git -C $root rev-list --count HEAD).Trim()
$tag = "v0.1.$count"
$zip = Get-ChildItem (Join-Path $root "dist") -Filter "SosOnline-0.1.$count+*.zip" | Sort-Object LastWriteTime | Select-Object -Last 1
if (-not $zip -or $zip.Name -like "*-modified*") { throw "Expected a clean dist\SosOnline-0.1.$count+<commit>.zip." }

if (-not $Notes) {
    $previous = gh release view --repo miguelguzmanr/sosonline --json tagName --jq .tagName 2>$null
    $range = if ($previous) { "$(git -C $root rev-list -n 1 $previous 2>$null)..HEAD" } else { "HEAD" }
    if ($range -like "..HEAD") { $range = "HEAD" }
    $Notes = (git -C $root log --format="- %s" $range | Select-Object -First 30) -join "`n"
}

gh release create $tag $zip.FullName --repo miguelguzmanr/sosonline --title "Sea of Stars Online Co-op $tag" --notes $Notes --target main
if ($LASTEXITCODE -ne 0) { throw "Release failed." }
Write-Host "Released $tag. Players see it as an available update at their next start."
