<#
.SYNOPSIS
  Prints the Sea of Stars install folder. Every tool, test script and the build use it.

.DESCRIPTION
  Looks, in order, at:
  1. -GameDir, when given;
  2. the SOS_GAME_DIR environment variable;
  3. game-dir.txt in the repository root (one line, the folder; gitignored, so each PC keeps its own);
  4. Steam's library folders (the Steam path from the registry, then steamapps\libraryfolders.vdf).
  A folder found by 1 or 4 is written to game-dir.txt when that file doesn't exist yet, so later runs find it at once.
  Fails if none of them holds SeaOfStars.exe.

.PARAMETER GameDir
  Sea of Stars install folder, overriding everything else.
#>
param([string]$GameDir)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$file = Join-Path $root "game-dir.txt"

function Test-GameDir([string]$dir) { $dir -and (Test-Path (Join-Path $dir "SeaOfStars.exe")) }

function Find-InSteam {
    $steam = (Get-ItemProperty "HKCU:\Software\Valve\Steam" -Name SteamPath -ErrorAction SilentlyContinue).SteamPath
    if (-not $steam) { return $null }
    $libraries = @($steam)
    $vdf = Join-Path $steam "steamapps\libraryfolders.vdf"
    if (Test-Path $vdf) {
        foreach ($m in [regex]::Matches((Get-Content -Raw $vdf), '"path"\s+"([^"]+)"')) { $libraries += $m.Groups[1].Value -replace '\\\\', '\' }
    }
    foreach ($lib in $libraries) {
        $dir = Join-Path $lib "steamapps\common\Sea of Stars"
        if (Test-GameDir $dir) { return (Resolve-Path $dir).Path }
    }
    return $null
}

$found = $null; $remember = $false
if ($GameDir) {
    if (-not (Test-GameDir $GameDir)) { throw "SeaOfStars.exe not found in '$GameDir'." }
    $found = $GameDir; $remember = $true
}
elseif ($env:SOS_GAME_DIR) {
    if (-not (Test-GameDir $env:SOS_GAME_DIR)) { throw "SeaOfStars.exe not found in SOS_GAME_DIR ('$env:SOS_GAME_DIR')." }
    $found = $env:SOS_GAME_DIR
}
elseif (Test-Path $file) {
    $found = (Get-Content -TotalCount 1 $file).Trim()
    if (-not (Test-GameDir $found)) { throw "SeaOfStars.exe not found in '$found' (from $file). Fix or delete that file." }
}
else {
    $found = Find-InSteam; $remember = $true
    if (-not $found) { throw "Sea of Stars not found in Steam's libraries. Pass -GameDir, set SOS_GAME_DIR, or write the folder into $file." }
}

$found = $found.TrimEnd('\', '/')
if ($remember -and -not (Test-Path $file)) { Set-Content -NoNewline -Encoding utf8 $file $found }
$found
