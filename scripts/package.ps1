# Builds the two downloads:
#   build\EmbervilleMP-<ver>.zip            the mod alone: unzip into the game's BepInEx\plugins\ (players who have BepInEx 5)
#   build\EmbervilleMP-<ver>-with-BepInEx.zip  BepInEx 5.4.23.5 + the mod: unzip into the game folder (next to Emberville.exe)
# Only the multiplayer mod's own files and LICENSE.md go in (an explicit list), never the mods built on the API (emberville-mods: Party Tricks, Damage
# Colors are released on their own, from github.com/HardHeadHackerHead/emberville-mods); the zips are checked.
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
& (Join-Path $PSScriptRoot "build.ps1")
$game = if ($env:EMB_GAMEDIR) { $env:EMB_GAMEDIR } else { "D:\SteamLibrary\steamapps\common\Emberville Demo" }
$plugin = Join-Path $game "BepInEx\plugins\EmbervilleMP"
$files = @("EmbervilleMP.dll", "LiteNetLib.dll", "Steamworks.NET.dll", "steam_api64.dll") | ForEach-Object { Join-Path $plugin $_ }
$version = (Select-String -Path (Join-Path $root "src\EmbervilleMP\Plugin.cs") -Pattern 'Version = "([^"]+)"').Matches[0].Groups[1].Value
$out = Join-Path $root "build"
$stage = Join-Path $out "package"
Remove-Item -Recurse -Force $stage -ErrorAction SilentlyContinue

# 1. mod only
$modStage = Join-Path $stage "mod"
New-Item -ItemType Directory -Force "$modStage\EmbervilleMP" | Out-Null
Copy-Item $files "$modStage\EmbervilleMP\"
Copy-Item (Join-Path $root "LICENSE.md") "$modStage\EmbervilleMP\"
$modZip = Join-Path $out "EmbervilleMP-$version.zip"
Remove-Item $modZip -ErrorAction SilentlyContinue
Compress-Archive -Path "$modStage\*" -DestinationPath $modZip

# 2. BepInEx + mod (BepInEx makes its own default config on first start; the player's existing one is never overwritten)
$fullStage = Join-Path $stage "full"
Expand-Archive -Path (Join-Path $root "downloads\BepInEx_win_x64_5.4.23.5.zip") -DestinationPath $fullStage
New-Item -ItemType Directory -Force "$fullStage\BepInEx\plugins\EmbervilleMP" | Out-Null
Copy-Item $files "$fullStage\BepInEx\plugins\EmbervilleMP\"
Copy-Item (Join-Path $root "LICENSE.md") "$fullStage\BepInEx\plugins\EmbervilleMP\"
$fullZip = Join-Path $out "EmbervilleMP-$version-with-BepInEx.zip"
Remove-Item $fullZip -ErrorAction SilentlyContinue
Compress-Archive -Path "$fullStage\*" -DestinationPath $fullZip

# Nothing of the example mods in the multiplayer mod's downloads.
Add-Type -AssemblyName System.IO.Compression.FileSystem
foreach ($z in $modZip, $fullZip) {
    $zip = [System.IO.Compression.ZipFile]::OpenRead($z)
    try { $bad = @($zip.Entries | Where-Object { $_.FullName -match "PartyTricks|DamageColors" } | ForEach-Object { $_.FullName }) } finally { $zip.Dispose() }
    if ($bad.Count) { throw "$z contains an example mod: $($bad -join ', ')" }
    $zip = [System.IO.Compression.ZipFile]::OpenRead($z)
    try { $lic = @($zip.Entries | Where-Object { $_.Name -eq "LICENSE.md" }).Count } finally { $zip.Dispose() }
    if (-not $lic) { throw "$z has no LICENSE.md (the license says it travels with every copy)" }
    "{0}  ({1} KB)" -f $z, [int]((Get-Item $z).Length / 1KB)
}
