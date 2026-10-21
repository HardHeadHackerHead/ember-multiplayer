<#
ea-check: everything the mod needs from the game, and what another game build breaks.

  .\scripts\ea-check.ps1                                    inventory + self-check against the demo (expect: no problems)
  .\scripts\ea-check.ps1 -GameDir "D:\...\Emberville"       check another build (Early Access): what's missing or changed
  .\scripts\ea-check.ps1 -Prove                             inject known breaks into a copy of the demo's DLLs and check
                                                            that every one is caught (the copy lives in build\ea-check)

  -BaselineDir  the build the plugin is compiled and tested against (default: EMB_GAMEDIR, else the demo's Steam folder).
                If Steam ever updates the demo, point this at an untouched copy of it.
  -NoBuild      use the EmbervilleMP.dll already built (otherwise: build.ps1 -NoDeploy, which never touches a game folder)
  -Plugin       check another EmbervilleMP.dll (needs its .pdb next to it for file:line)
  -Out          report folder (default build\ea-check\self, build\ea-check\<GameDir name>, build\ea-check\prove)
  -Snapshot     also copy the inventory to docs\EA-INVENTORY.md (the committed demo snapshot)

Writes <Out>\inventory.md (every dependency, by feature and file) and <Out>\check.md (problems, most severe first).
Exit code: 0 no problems, 1 problems found, 2 tool error. Reads game folders only; never writes into one.
#>
param(
    [string]$GameDir,
    [string]$BaselineDir = $(if ($env:EMB_GAMEDIR) { $env:EMB_GAMEDIR } else { "D:\SteamLibrary\steamapps\common\Emberville Demo" }),
    [string]$Plugin,
    [string]$Out,
    [switch]$NoBuild,
    [switch]$Prove,
    [switch]$Snapshot
)
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$tool = Join-Path $root "tools\EaCheck"
$toolDll = Join-Path $tool "bin\Release\net8.0\EaCheck.dll"
$BaselineDir = (Resolve-Path $BaselineDir).Path

function Assert-NotInGame([string]$path) {
    $full = [IO.Path]::GetFullPath($path).TrimEnd('\') + '\'
    foreach ($g in @($BaselineDir, $GameDir) | Where-Object { $_ }) {
        $gf = [IO.Path]::GetFullPath($g).TrimEnd('\') + '\'
        if ($full.StartsWith($gf, [StringComparison]::OrdinalIgnoreCase)) { throw "refusing to write into a game folder: $path" }
    }
}

# the tool
$build = dotnet build (Join-Path $tool "EaCheck.csproj") -c Release -nologo -v q 2>&1
if ($LASTEXITCODE -ne 0) { $build | Select-String "error" | ForEach-Object { $_.Line }; throw "EaCheck build failed" }

# the plugin, compiled against the baseline
if (-not $Plugin) {
    if (-not $NoBuild) {
        $saved = $env:EMB_GAMEDIR
        $env:EMB_GAMEDIR = $BaselineDir
        try { & (Join-Path $PSScriptRoot "build.ps1") -NoDeploy | Out-Null } finally { $env:EMB_GAMEDIR = $saved }
    }
    $Plugin = Join-Path $root "src\EmbervilleMP\bin\Release\netstandard2.1\EmbervilleMP.dll"
}
if (-not (Test-Path $Plugin)) { throw "no plugin at $Plugin (build it, or drop -NoBuild)" }

function Invoke-EaCheck([string]$target, [string]$outDir) {
    Assert-NotInGame $outDir
    $a = @("run", "--plugin", $Plugin, "--src", (Join-Path $root "src\EmbervilleMP"), "--baseline", $BaselineDir,
           "--hints", (Join-Path $tool "ea-hints.txt"), "--out", $outDir)
    if ($target) { $a += @("--target", $target) }
    & dotnet $toolDll @a | Out-Host
    return $LASTEXITCODE
}

if ($Prove) {
    # A copy of the baseline's DLLs with known breaks, one per kind of dependency.
    $fake = Join-Path $root "build\ea-check\prove-game"
    Assert-NotInGame $fake
    if (Test-Path $fake) { Remove-Item -Recurse -Force $fake }
    $data = Get-ChildItem $BaselineDir -Directory -Filter "*_Data" | Where-Object { Test-Path (Join-Path $_.FullName "Managed\Assembly-CSharp.dll") } | Select-Object -First 1
    $fakeManaged = Join-Path $fake "$($data.Name)\Managed"
    New-Item -ItemType Directory -Force $fakeManaged | Out-Null
    Copy-Item (Join-Path $data.FullName "Managed\*.dll") $fakeManaged
    $asm = Join-Path $fakeManaged "Assembly-CSharp.dll"
    Move-Item $asm "$asm.orig"
    $ops = @(
        "rename-method:GateDoor::Open=OpenGate",                                      # hook target gone + a call we make
        "add-overload:Chest::CloseChest",                                             # hook target now ambiguous
        "rename-param:DialogueTrigger::SetVariableValue:key=variableKey",             # patch parameter bound by name
        "rename-type:SpiderEgg=SpiderEggV2",                                          # a type we use everywhere in EggSync
        "rename-field:Enemy::HealthChanged=OnHealthChanged",                          # FieldRefAccess by name
        "rename-field:RepairableBuilding::transitionSfx=transitionSound",             # dynamic lookup listed in ea-hints.txt
        "enum:BoolID.MetAtwor=9999",                                                  # enum value (test-only use)
        "enum:DoorState.Opened=7",                                                    # enum value (runtime use)
        "swap-call:ShooterTrap::LateUpdate:UnityEngine.Time::get_time=get_unscaledTime", # transpiler anchor
        "param-default:SceneLoader::CallScene:fallbackStartScene=Hub",                # optional parameter default baked into our IL
        "add-interface-method:IDamageable::NewAbstractMember"                         # our classes implementing it stop loading
    )
    $mutArgs = @("mutate", "--in", "$asm.orig", "--out", $asm)
    foreach ($o in $ops) { $mutArgs += @("--op", $o) }
    & dotnet $toolDll @mutArgs | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "mutate failed" }
    Remove-Item "$asm.orig"
    & dotnet $toolDll rename-scene --in (Join-Path $data.FullName "globalgamemanagers") --out (Join-Path $fake "$($data.Name)\globalgamemanagers") --from TutorialArea --to TutorialZone | Out-Host
    Copy-Item (Join-Path $data.FullName "app.info") (Join-Path $fake $data.Name) -ErrorAction SilentlyContinue

    if (-not $Out) { $Out = Join-Path $root "build\ea-check\prove" }
    $code = Invoke-EaCheck $fake $Out
    $report = Get-Content (Join-Path $Out "check.md") -Raw
    $expect = [ordered]@{
        "hook target renamed"              = "hook DoorOpenPatch can't find GateDoor.Open"
        "call to a renamed method"         = "method GateDoor.Open() is missing"
        "hook target ambiguous"            = "hook ChestClosePatch can't find Chest.CloseChest"
        "patch parameter renamed"          = "parameter 'key' no longer exists in DialogueTrigger.SetVariableValue"
        "type renamed"                     = "type SpiderEgg is missing"
        "lookup by name (FieldRefAccess)"  = "lookup by name fails: Enemy.HealthChanged"
        "dynamic lookup (ea-hints.txt)"    = "RepairableBuilding.transitionSfx"
        "enum value, test-only"            = "BoolID.MetAtwor changed value"
        "enum value, runtime"              = "DoorState.Opened changed value 1"
        "transpiler anchor"                = "no longer uses UnityEngine.Time.time"
        "optional parameter default"       = "default of 'fallbackStartScene' changed"
        "new interface member"             = "doesn't implement IDamageable.NewAbstractMember"
        "scene renamed"                    = 'scene "TutorialArea" is not in this build'
    }
    $missed = 0
    foreach ($k in $expect.Keys) {
        $hit = $report.Contains($expect[$k])
        if (-not $hit) { $missed++ }
        "{0,-6} {1}" -f $(if ($hit) { "caught" } else { "MISSED" }), $k
    }
    if ($code -ne 1) { "unexpected exit code $code (expected 1: problems found)"; $missed++ }
    if ($missed -gt 0) { "PROVE FAILED: $missed break(s) not caught"; exit 1 }
    "PROVE OK: every injected break was caught (report: $(Join-Path $Out 'check.md'))"
    exit 0
}

if (-not $Out) {
    $name = if ($GameDir) { (Split-Path ([IO.Path]::GetFullPath($GameDir).TrimEnd('\')) -Leaf) -replace '[^A-Za-z0-9._-]', '_' } else { "self" }
    $Out = Join-Path $root "build\ea-check\$name"
}
$code = Invoke-EaCheck $GameDir $Out
if ($Snapshot) {
    Copy-Item (Join-Path $Out "inventory.md") (Join-Path $root "docs\EA-INVENTORY.md")
    "Snapshot: docs\EA-INVENTORY.md"
}
exit $code
