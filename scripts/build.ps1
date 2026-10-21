# Builds the plugin and deploys it into the game's BepInEx\plugins\EmbervilleMP. Throws on failure.
#   -NoDeploy (or EMB_NODEPLOY=1): compile only (checks the code without touching any game folder)
#   -WithMods: also deploy the mods built on our API (Party Tricks, Damage Colors: their own public repo,
#              emberville-mods, next to this one or at $env:EMB_MODS) into their own plugin folders
#              (BepInEx\plugins\PartyTricks\...), for tests. They're always compiled (after the plugin: they reference
#              its DLL), never deployed without this, and never go into EmbervilleMP's folder or its release zips.
param([switch]$NoDeploy, [switch]$WithMods)
$root = Split-Path $PSScriptRoot -Parent
$gameArg = if ($env:EMB_GAMEDIR) { "-p:GameDir=$($env:EMB_GAMEDIR)" } else { "-p:GameDir=D:\SteamLibrary\steamapps\common\Emberville Demo" }
$noDeploy = $NoDeploy -or $env:EMB_NODEPLOY
$deployArg = if ($noDeploy) { "-p:NoDeploy=true" } else { "-p:NoDeploy=false" }
$out = dotnet build (Join-Path $root "src\EmbervilleMP\EmbervilleMP.csproj") -c Release -nologo $gameArg $deployArg 2>&1
$errors = $out | Select-String " error " | Select-Object -Unique
if ($LASTEXITCODE -ne 0 -or $errors) {
    $errors | ForEach-Object { $_.Line }
    throw "build failed"
}
# The mods built on our API: their own repo (github.com/HardHeadHackerHead/emberville-mods), compiled against this build.
$modsRepo = $(if ($env:EMB_MODS) { $env:EMB_MODS } else { Join-Path (Split-Path $root -Parent) "emberville-mods" })
if (Test-Path (Join-Path $modsRepo "scripts\build.ps1")) {
    $game = $(if ($env:EMB_GAMEDIR) { $env:EMB_GAMEDIR } else { "D:\SteamLibrary\steamapps\common\Emberville Demo" })
    & (Join-Path $modsRepo "scripts\build.ps1") -GameDir $game -MultiplayerDir (Join-Path $root "src\EmbervilleMP\bin\Release\netstandard2.1") -Deploy:($WithMods -and -not $noDeploy) | Out-Null
} else {
    "(no emberville-mods next to this repo: the mods built on the API aren't compiled or tested; clone it, or set EMB_MODS)"
}
"build ok"
