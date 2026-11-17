#Requires -Version 7
<#
.SYNOPSIS
  Builds the plugin and writes the zip players install, signed (nothing is uploaded; publish-release.ps1 does that).

.DESCRIPTION
  Writes dist\SosOnline-<version>.zip with BepInEx\plugins\SosOnline\SosOnline.dll inside, and SosOnline.dll.sig next
  to it when the release signing key exists (%APPDATA%\SosOnline\release-signing-key.txt, see new-signing-key.ps1).
  The in-game updater only installs a signed DLL. Extract the zip into the game folder (after BepInEx is installed
  there) so the DLL lands in BepInEx\plugins\SosOnline. Everyone in a session needs the same build.

.PARAMETER GameDir
  Sea of Stars install folder. Optional: tools\game-dir.ps1 finds it (SOS_GAME_DIR, game-dir.txt, or Steam's libraries).
  It needs BepInEx and its interop assemblies (to build).
#>
param(
    [string]$GameDir
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$GameDir = & "$PSScriptRoot\game-dir.ps1" -GameDir $GameDir

if (-not (Test-Path "$GameDir\BepInEx\interop\Assembly-CSharp.dll")) {
    throw "BepInEx interop assemblies are missing in '$GameDir'. Run tools\install-to-game.ps1 first."
}

dotnet build "$root\SosOnline\SosOnline.csproj" -c Release -p:GameDir="$GameDir" -p:DeployToGame=false
if ($LASTEXITCODE -ne 0) { throw "Build failed." }

$dllPath = "$root\SosOnline\bin\Release\SosOnline.dll"
$version = (Get-Item $dllPath).VersionInfo.ProductVersion   # 0.1.<count>+<commit>[-modified]

$stage = Join-Path $root "dist\stage"
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
$plugin = New-Item -ItemType Directory -Force "$stage\BepInEx\plugins\SosOnline"
Copy-Item $dllPath $plugin

$keyPath = Join-Path $env:APPDATA "SosOnline\release-signing-key.txt"
if (Test-Path $keyPath) {
    $key = [System.Security.Cryptography.ECDsa]::Create()
    $bytesRead = 0
    $key.ImportPkcs8PrivateKey([Convert]::FromBase64String((Get-Content -Raw $keyPath).Trim()), [ref]$bytesRead)
    $signature = $key.SignData([IO.File]::ReadAllBytes($dllPath), [System.Security.Cryptography.HashAlgorithmName]::SHA256)
    [IO.File]::WriteAllBytes("$plugin\SosOnline.dll.sig", $signature)
    Write-Host "Signed with $keyPath"
} else {
    Write-Warning "No signing key at ${keyPath}: the in-game updater won't install this zip."
}

$zip = Join-Path $root "dist\SosOnline-$version.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path "$stage\BepInEx" -DestinationPath $zip
Remove-Item $stage -Recurse -Force
Write-Host "Packed $zip"
