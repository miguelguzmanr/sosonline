#Requires -Version 7
<#
.SYNOPSIS
  Creates the key that signs released builds (run once).

.DESCRIPTION
  The in-game updater only installs a DLL signed with this key. The private key is written to
  %APPDATA%\SosOnline\release-signing-key.txt (outside the repository: never commit it, keep a backup). The public key
  is printed: paste it into SosOnline\Update\AutoUpdate.cs (PublicKey) and release a build with it before relying on
  the in-game updater. Losing the private key means players have to install the next build by hand.
#>
param([switch]$Force)
$ErrorActionPreference = "Stop"

$dir = Join-Path $env:APPDATA "SosOnline"
$path = Join-Path $dir "release-signing-key.txt"
if ((Test-Path $path) -and -not $Force) { throw "A signing key already exists at $path. Use -Force to replace it (players then need one manual update)." }

New-Item -ItemType Directory -Force $dir | Out-Null
$key = [System.Security.Cryptography.ECDsa]::Create([System.Security.Cryptography.ECCurve+NamedCurves]::nistP256)
[Convert]::ToBase64String($key.ExportPkcs8PrivateKey()) | Set-Content -NoNewline -Encoding ascii $path
Write-Host "Private key: $path (back it up; never share or commit it)"
Write-Host "Public key (for AutoUpdate.PublicKey):"
Write-Host ([Convert]::ToBase64String($key.ExportSubjectPublicKeyInfo()))
