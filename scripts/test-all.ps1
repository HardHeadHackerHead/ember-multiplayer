# Runs the self-tests, headless by default. The scenarios and what they need come from their [Scenario(...)]
# attributes in src\EmbervilleMP\Dev\SelfTest*.cs (Timeout, Players, Start, Scene, Switches, Quick, Fresh, Covers), and
# the API mods' [MpScenario(...)] in ..\emberville-mods\mods\**\*.cs (deployed for the tests: build.ps1 -WithMods): no list here.
# Scenarios with the same player count run as chains, one boot for many: a fresh world before each, its
# switches set at runtime, the guests leaving and joining again when a scenario starts from their own world or the menu.
#   .\scripts\test-all.ps1 [-Quick] [-Changed [-Base main]] [-Only presence,combat] [-Shuffle [-Seed n]] [-Plan]
#                          [-Parallel 2] [-MaxCopies 5] [-MinFreeGB 3] [-TimeScale 1] [-Transport lan|steamip]
#                          [-Windowed] [-Budget 3600] [-ChainSize n] [-NoChain] [-NoFastJoin] [-Lag "60,3"]
# -Quick: the everyday check; the whole suite before merging to main.
# -Changed: the scenarios that cover what changed since -Base (commits, the working tree, new files), and -Quick;
#  a change to the harness or the core (session, runner, protocol) runs everything. Says why each one is picked.
# -Plan: only list the runs (which scenarios share a boot, in which order), without building or starting anything.
# -Shuffle: chains in a random order (-Seed to repeat one): catches a scenario that leaves something behind.
# -Parallel: runs at once, but never more than -MaxCopies game copies (each ~3 GB) and only with -MinFreeGB to spare.
# -TimeScale: the game runs this much faster in tests (--mp-timescale): gameplay and the game's timers, not ours.
# -Budget: seconds of scenario time limits per run (runs are cut by it); -ChainSize: at most this many per run.
# -NoFastJoin: guests boot on the main menu and join from there (default: they load their own world while the host
#  loads, then join: shorter boots).
param(
    [string[]]$Only = @(),
    [switch]$Quick,
    [switch]$Changed,
    [string]$Base = "main",
    [string[]]$Files = @(),   # with -Changed: these files instead of git's list (what would a change to X run?)
    [switch]$Plan,
    [switch]$Shuffle,
    [int]$Seed = 0,
    [ValidateSet("lan", "steamip")][string]$Transport = "lan",
    [switch]$Windowed,
    [int]$Parallel = 2,
    [int]$MaxCopies = 5,
    [double]$MinFreeGB = 3,     # (on top of ~3.2 GB for each copy the new run starts)
    [double]$TimeScale = 1,
    [int]$Budget = 3600,
    [int]$ChainSize = 0,
    [switch]$NoChain,
    [switch]$NoFastJoin,
    [string]$Lag = ""        # e.g. "60,3": 60 ms one-way (~120 ms round trip), 3% loss of unreliable messages
)
$ErrorActionPreference = "Stop"
$Only = @($Only | ForEach-Object { $_ -split "," } | Where-Object { $_ })   # (-File passes "a,b" as one string)
if (-not $Plan) { & (Join-Path $PSScriptRoot "build.ps1") -WithMods }
$repo = Split-Path $PSScriptRoot -Parent
$src = Join-Path $repo "src\EmbervilleMP"
$modsDir = Join-Path $(if ($env:EMB_MODS) { $env:EMB_MODS } else { Join-Path (Split-Path $repo -Parent) "emberville-mods" }) "mods"

# ---- the scenarios, from their attributes (one per line: [Scenario("name", Timeout = 200, Players = 3, ...)]) ----
$all = @()
$scenarioFiles = @(Get-ChildItem (Join-Path $src "Dev") -Filter "SelfTest*.cs") +
                 @(Get-ChildItem $modsDir -Recurse -Filter "*.cs" -ErrorAction SilentlyContinue | Where-Object { $_.FullName -notmatch "\\(bin|obj)\\" })
foreach ($file in $scenarioFiles) {
    foreach ($line in Get-Content $file.FullName) {
        if ($line -notmatch '^\s*\[(?:Scenario|MpScenario)\((.*)\)\]\s*$') { continue }
        $a = $Matches[1]
        $props = @{}
        foreach ($p in [regex]::Matches($a, '\b([A-Z]\w*)\s*=\s*("([^"]*)"|[^,]+)')) {
            $props[$p.Groups[1].Value] = $(if ($p.Groups[3].Success) { $p.Groups[3].Value } else { $p.Groups[2].Value.Trim() })
        }
        if (-not $props.Timeout) { continue }   # surveys, tools, screenshots
        $names = [regex]::Matches(($a -replace '\b[A-Z]\w*\s*=.*$', ''), '"([^"]+)"') | ForEach-Object { $_.Groups[1].Value }
        foreach ($n in $names) {
            $all += [pscustomobject]@{ Name = $n; Timeout = [int]$props.Timeout; Players = $(if ($props.Players) { [int]$props.Players } else { 2 })
                                       Start = [string]$props.Start; Scene = [string]$props.Scene; Switches = [string]$props.Switches
                                       Quick = $props.Quick -eq "true"; Covers = [string]$props.Covers; Isolate = [string]$props.Isolate; File = $file.Name
                                       Path = $file.FullName; Mod = $(if ($file.FullName.StartsWith($modsDir)) { $file.FullName.Substring($modsDir.Length + 1).Replace("\", "/").Split("/")[0] } else { "" }) }
        }
    }
}

# ---- -Changed: what the changes touch ----
# A scenario covers its own file, the mod's types its file names (EnemySync, LootRules...: whatever it reads or calls)
# and its Covers paths; a changed Patches/ or UI/ file reaches the types it names in turn. The harness and the core
# reach everything.
$core = @("Dev/SelfTest.cs", "Dev/SelfTest.Chain.cs", "Dev/DevHarness*.cs", "Dev/FastDeath.cs", "Dev/Watchdog.cs", "Dev/FileLog.cs",
          "Args.cs", "Plugin.cs", "MPRunner.cs", "Game.cs", "Net/*", "Sync/Presence*.cs", "Sync/GuestWorld.cs", "Sync/SceneAuthority.cs",
          "EmbervilleMP.csproj")
function Get-Picked {
    $files = @($Files | ForEach-Object { $_ -split "," } | Where-Object { $_ } | ForEach-Object { $_.Replace("\", "/") })
    if (-not $files.Count) { try {
        $files += git -C $repo diff --name-only "$Base...HEAD" 2>$null
        $files += git -C $repo diff --name-only HEAD 2>$null
        $files += git -C $repo ls-files --others --exclude-standard 2>$null
    } catch { } }
    $files = @($files | Where-Object { $_ } | Sort-Object -Unique)
    $why = [ordered]@{}
    function Add-Why($name, $reason) { if (-not $why.Contains($name)) { $why[$name] = $reason } }
    if ($files | Where-Object { $_ -like "scripts/run-pair.ps1" -or $_ -like "scripts/launch.ps1" }) {
        foreach ($t in $all) { Add-Why $t.Name "the test scripts changed" }
        return $why
    }
    # the mod's types, by file
    $declared = @{}
    foreach ($f in Get-ChildItem $src -Recurse -Filter "*.cs" | Where-Object { $_.FullName -notmatch "\\(obj|bin)\\" }) {
        $rel = $f.FullName.Substring($src.Length + 1).Replace("\", "/")
        # (types in the namespace itself: nested ones have everyday names, Kind, Entry, Cause)
        $declared[$rel] = @([regex]::Matches((Get-Content $f.FullName -Raw), '(?m)^ {4}(?:(?:public|internal|private|protected|static|sealed|abstract|partial|readonly)\s+)*(?:class|struct|enum|interface)\s+(\w+)') | ForEach-Object { $_.Groups[1].Value })
    }
    $testText = @{}
    foreach ($t in $all) { if (-not $testText.ContainsKey($t.Path)) { $testText[$t.Path] = Get-Content $t.Path -Raw } }
    $coreTypes = @("Session", "Game", "Plugin", "Args", "MPRunner", "Chat", "Protocol", "NetIds", "PlayerInfo", "Msg", "SelfTest", "DevHarness")
    foreach ($f in $files) {
        # the API: the mods built on it (their own repo) run their scenarios too
        if ($f -like "src/EmbervilleMP/Api/*") {
            foreach ($t in ($all | Where-Object { $_.Mod })) { Add-Why $t.Name "the API they use changed ($f)" }
        }
        if ($f -notlike "src/EmbervilleMP/*") { continue }
        $rel = $f.Substring("src/EmbervilleMP/".Length)
        if ($core | Where-Object { $rel -like $_ }) {
            foreach ($t in $all) { Add-Why $t.Name "core file $rel" }
            continue
        }
        $leaf = Split-Path $rel -Leaf
        foreach ($t in $all) {
            if ($t.File -eq $leaf) { Add-Why $t.Name "its own file $rel" }
            foreach ($c in ($t.Covers -split "\s*,\s*" | Where-Object { $_ })) { if ($rel -like $c) { Add-Why $t.Name "covers $c" } }
        }
        if ($rel -notlike "*.cs" -or -not $declared.ContainsKey($rel)) { continue }
        $types = @($declared[$rel])
        if ($rel -like "Patches/*" -or $rel -like "UI/*") {
            # one hop: the mod's types the patch or the UI names
            $text = Get-Content (Join-Path $src $rel) -Raw
            # and by name: Patches/DeathPatches.cs is about Deaths, DeathBanner...
            $stem = [IO.Path]::GetFileNameWithoutExtension($rel) -replace "Patches$", ""
            if ($stem.Length -ge 4) { foreach ($kv in $declared.GetEnumerator()) { if ($kv.Key -notlike "Dev/*") { $types += @($kv.Value | Where-Object { $_ -like "$stem*" }) } } }
            foreach ($kv in $declared.GetEnumerator()) {
                if ($kv.Key -like "Patches/*" -or $kv.Key -like "Dev/*") { continue }
                foreach ($n in $kv.Value) { if ($n -notin $coreTypes -and $text -match "\b$n\b") { $types += $n } }
            }
        }
        foreach ($n in ($types | Where-Object { $_ -notin $coreTypes } | Sort-Object -Unique)) {
            foreach ($t in $all) { if ($testText[$t.Path] -match "\b$n\b") { Add-Why $t.Name "uses $n ($rel)" } }
        }
    }
    foreach ($t in ($all | Where-Object { $_.Quick })) { Add-Why $t.Name "-Quick" }
    return $why
}

$picked = $null
if ($Changed) {
    $picked = Get-Picked
    "changed since ${Base}: $($picked.Count) of $($all.Count) scenarios"
    foreach ($kv in $picked.GetEnumerator()) { "  {0,-20} {1}" -f $kv.Key, $kv.Value }
}
$tests = @(if ($Only.Count) { $all | Where-Object { $Only -contains $_.Name } }
           elseif ($picked) { $all | Where-Object { $picked.Contains($_.Name) } }
           elseif ($Quick) { $all | Where-Object { $_.Quick } }
           else { $all })
$rng = $null
if ($Shuffle) {
    if (-not $Seed) { $Seed = Get-Random -Minimum 1 -Maximum 1000000 }
    $rng = New-Object System.Random $Seed
    "shuffled, seed $Seed (-Shuffle -Seed $Seed repeats it)"
}


# ---- runs ----
# One run: its scenarios' own switches and time limits go in --mp-scenario-args ("name:key=value/key;name2:...").
function New-Run($part) {
    $first = $part[0]
    $spec = ($part | ForEach-Object {
        $sw = @("scenario-timeout=$($_.Timeout)") + @($_.Switches -split "\s+" | Where-Object { $_ } | ForEach-Object { $_ -replace "^--mp-", "" })
        if ($_.Scene) { $sw += "autostart=$($_.Scene)" }
        "$($_.Name):$($sw -join '/')"
    }) -join ";"
    @{ Names = @($part | ForEach-Object { $_.Name }); Guests = $first.Players - 1; Spec = $spec
       MenuFlow = $first.Start -eq "menus"
       # the guests' boot: their own world first (then join), or the main menu
       JoinLater = ($first.Start -eq "own-world") -or (-not $NoFastJoin -and -not $first.Scene -and $first.Start -notin @("menu", "menus"))
       # (a chain's fresh worlds, barriers and rejoins come on top of its scenarios' own times)
       Timeout = (($part | ForEach-Object { $_.Timeout } | Measure-Object -Sum).Sum + 20 * ($part.Count - 1)) }
}

# Who may open a run: not one whose guests must come from the menu (the boot decides that) or that starts in another
# scene (the guests' own world would be there too).
function Test-Opener($t) { $t.Start -ne "menu" -and -not $t.Scene }

$runs = @()
foreach ($t in ($tests | Where-Object { $NoChain -or $_.Start -eq "menus" })) { $runs += New-Run @($t) }
# (Isolate: a run of its own, shared only with the scenarios of the same name)
foreach ($group in ($tests | Where-Object { -not $NoChain -and $_.Start -ne "menus" } | Group-Object { "$($_.Players)|$($_.Isolate)" })) {
    $items = @($group.Group | Sort-Object { if ($rng) { $rng.Next() } else { -$_.Timeout } })
    $boot = @($items | Where-Object { $_.Start -eq "boot" })
    $rest = @($items | Where-Object { $_.Start -ne "boot" })
    $total = ($items | ForEach-Object { $_.Timeout } | Measure-Object -Sum).Sum
    $n = [int][Math]::Max([Math]::Max($boot.Count, [Math]::Ceiling($total / $Budget)), 1)
$1
    if ($items[0].Isolate) { $n = [int][Math]::Max($boot.Count, 1) }
    $chains = @{}; $weight = @{}
    for ($i = 0; $i -lt $n; $i++) { $chains[$i] = New-Object System.Collections.ArrayList; $weight[$i] = 0 }
    for ($i = 0; $i -lt $boot.Count; $i++) { [void]$chains[$i].Add($boot[$i]); $weight[$i] += $boot[$i].Timeout }
    foreach ($t in $rest) {
        $k = 0..($n - 1) | Sort-Object { $weight[$_] }, { $chains[$_].Count } | Select-Object -First 1
        [void]$chains[$k].Add($t); $weight[$k] += $t.Timeout
    }
    for ($i = 0; $i -lt $n; $i++) {
        $c = $chains[$i]
        if (-not $c.Count) { continue }
        # an opener first (a boot scenario is one already)
        if (-not (Test-Opener $c[0]) -and $c[0].Start -ne "boot") {
            $j = 0; while ($j -lt $c.Count -and -not (Test-Opener $c[$j])) { $j++ }
            if ($j -lt $c.Count) { $o = $c[$j]; $c.RemoveAt($j); $c.Insert(0, $o) }
        }
        $runs += New-Run @($c)
    }
}
# longest first, so a parallel pool ends together
$runs = @($runs | Sort-Object { $_.Timeout } -Descending)
if ($Plan) {
    $i = 0
    $runs | ForEach-Object { $i++; [pscustomobject]@{ Run = $i; Players = $_.Guests + 1; GuestsBoot = $(if ($_.MenuFlow) { "menus" } elseif ($_.JoinLater) { "own world" } else { "menu" }); Timeout = $_.Timeout; Count = $_.Names.Count; Scenarios = ($_.Names -join ",") } } |
        Format-Table -AutoSize -Wrap
    "$($tests.Count) scenarios in $($runs.Count) runs"
    return
}

$results = @()
$runTimes = @()
$start = Get-Date
$running = @()
$pending = New-Object System.Collections.ArrayList
$runs | ForEach-Object { [void]$pending.Add($_) }
$index = @{}; for ($i = 0; $i -lt $runs.Count; $i++) { $index[$runs[$i]] = $i + 1 }
$slot = 0
$lastStart = [datetime]::MinValue
# Free memory in GB (each headless copy takes ~3 GB once in game).
function Get-FreeGB { try { (Get-CimInstance Win32_OperatingSystem).FreePhysicalMemory / 1MB } catch { 999 } }
while ($pending.Count -or $running.Count) {
    while ($pending.Count -and $running.Count -lt $Parallel) {
        # the longest run that fits the copy budget (one always may start when nothing runs)
        $busy = ($running | ForEach-Object { $_.Run.Guests + 1 } | Measure-Object -Sum).Sum
        $r = $pending | Where-Object { -not $running.Count -or $busy + $_.Guests + 1 -le $MaxCopies } | Select-Object -First 1
        if (-not $r) { break }
        if ($running.Count) {
            # the copies started last are still growing: let them settle, then look at what is left
            if (((Get-Date) - $lastStart).TotalSeconds -lt 40) { break }
            if ((Get-FreeGB) -lt $MinFreeGB + 3.2 * ($r.Guests + 1)) { break }
        }
        [void]$pending.Remove($r)
        $lastStart = Get-Date
        $port = $(if ($env:EMB_PORTBASE) { [int]$env:EMB_PORTBASE } else { 27130 }) + ($slot % 20)
        $tag = "t$slot"
        $slot++
        # (built one by one: two empty $(...) in a row empty the whole @(...) list in PowerShell 5.1)
        $items = @()
        if ($Lag) { $items += "--mp-lag=$Lag" }
        if ($TimeScale -ne 1) { $items += "--mp-timescale=$TimeScale" }
        $items += "--mp-scenario-args=$($r.Spec)"
        $extra = ,$items
        $job = Start-Job -ArgumentList $PSScriptRoot, ($r.Names -join ","), $r.Timeout, $tag, $port, (-not $Windowed), $Transport, ([bool]$r.JoinLater), $r.Guests, ([bool]$r.MenuFlow), $extra -ScriptBlock {
            param($dir, $names, $timeout, $tag, $port, $headless, $transport, $joinLater, $guests, $menuFlow, $extra)
            & (Join-Path $dir "run-pair.ps1") $names -Scene TutorialArea -TimeoutSec $timeout -Tag $tag -Port $port -Fresh -Headless:$headless -Transport $transport -JoinLater:$joinLater -Guests $guests -MenuFlow:$menuFlow -Extra $extra
        }
        $running += [pscustomobject]@{ Job = $job; Run = $r; Started = Get-Date; Index = $index[$r] }
        # (starts in one game folder are serialized by launch.ps1's lock)
        if ($Parallel -gt 1) { Start-Sleep -Seconds 1 }
    }
    Start-Sleep -Seconds 1
    foreach ($x in @($running)) {
        $timedOut = ((Get-Date) - $x.Started).TotalSeconds -gt ($x.Run.Timeout + 180)
        if ($x.Job.State -eq "Running" -and -not $timedOut) { continue }
        if ($timedOut) { Stop-Job $x.Job }
        $wall = ((Get-Date) - $x.Started).TotalSeconds
        $lines = (Receive-Job $x.Job 2>&1 | Out-String) -split "`n"
        Remove-Job $x.Job -Force
        $running = @($running | Where-Object { $_ -ne $x })
        $chain = $x.Run.Names.Count -gt 1
        # "[MP] TIME name 12.3 s (...)": each scenario's own time (the host's); "[MP] TIME in-game 25.1 s": each copy's boot
        $times = @{}
        $inGame = 0.0
        foreach ($l in $lines) {
            if ($l -match "-host\] .*\[MP\] TIME (\S+) ([\d.]+) s \(") { if (-not $times.ContainsKey($Matches[1])) { $times[$Matches[1]] = [double]$Matches[2] } }
            elseif ($l -match "\[MP\] TIME in-game ([\d.]+) s") { $inGame = [Math]::Max($inGame, [double]$Matches[1]) }
        }
        $runTimes += [pscustomobject]@{ Run = $x.Index; Scenarios = $x.Run.Names.Count; Wall = [int]$wall; InGame = [int]$inGame
                                        Scenario = [int](($times.Values | Measure-Object -Sum).Sum); Names = ($x.Run.Names -join ",") }
        foreach ($name in $x.Run.Names) {
            $mine = @($lines | Where-Object { $_ -match "\[MP\] RESULT (PASS|FAIL) $([regex]::Escape($name)):" })
            $pass = $mine.Count -gt 0 -and -not ($mine | Where-Object { $_ -match "RESULT FAIL" }) -and ($mine.Count -ge (1 + $x.Run.Guests))
            $tagged = if ($chain) { "CHECK FAIL \[$([regex]::Escape($name))\]" } else { "CHECK FAIL" }
            $fails = @($lines | Where-Object { $_ -match "$tagged|RESULT FAIL $([regex]::Escape($name)):" -or (-not $chain -and $_ -match "TIMEOUT") })
            if (-not $mine.Count) { $fails += "no result" + $(if ($timedOut) { " (timed out)" } else { "" }) }
            $secs = if ($times.ContainsKey($name)) { [int]$times[$name] } else { $null }
            $results += [pscustomobject]@{ Test = $name; Run = $x.Index; Secs = $secs; Result = $(if ($pass) { "PASS" } else { "FAIL" }); Details = (($fails | ForEach-Object { $_.Trim() }) -join " | ") }
        }
    }
}
$results | Sort-Object Test | Format-Table -AutoSize -Wrap
# Where the time goes, per run: wall clock, when the last copy got in game (the boot), the scenarios' own seconds.
$runTimes | Sort-Object Run | Format-Table Run, Scenarios, Wall, InGame, Scenario, Names -AutoSize -Wrap
"slowest: " + (($results | Where-Object { $_.Secs } | Sort-Object Secs -Descending | Select-Object -First 10 | ForEach-Object { "$($_.Test) $($_.Secs)s" }) -join ", ")
if ($Shuffle) { "shuffled, seed $Seed" }
"$($results.Count) scenarios in $($runs.Count) runs; transport $Transport, $(if ($Windowed) { 'windowed' } else { 'headless' }), total {0:N0}s" -f ((Get-Date) - $start).TotalSeconds
if ($results | Where-Object Result -ne "PASS") { exit 1 }
