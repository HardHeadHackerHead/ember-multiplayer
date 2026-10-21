# Shared helpers for the test scripts: start game instances with --mp-* switches and collect their logs.
# EMB_GAMEDIR: another copy of the game (parallel work: each worker builds into and tests its own copy)
$script:GameDir = if ($env:EMB_GAMEDIR) { $env:EMB_GAMEDIR } else { "D:\SteamLibrary\steamapps\common\Emberville Demo" }
$script:Root = Split-Path $PSScriptRoot -Parent

function Start-Instance {
    param(
        [Parameter(Mandatory)][string]$Tag,
        [string[]]$MpArgs = @(),
        [int]$Width = 640, [int]$Height = 360,
        [string]$OutDir,
        [switch]$Headless
    )
    if (-not $OutDir) { $OutDir = Join-Path $script:Root "build\test\$Tag" }
    New-Item -ItemType Directory -Force $OutDir | Out-Null
    $log = Join-Path $OutDir "player.log"
    Remove-Item $log, (Join-Path $OutDir "mp.log") -ErrorAction SilentlyContinue
    $all = @("-screen-fullscreen", "0", "-screen-width", "$Width", "-screen-height", "$Height", "-logFile", "`"$log`"",
             "--mp-out=`"$OutDir`"", "--mp-window=$($Width)x$($Height)") + $MpArgs
    # No window, no GPU: faster to start, many copies at once. Screenshots don't work in this mode.
    if ($Headless) { $all = @("-batchmode", "-nographics", "--mp-fps=60") + $all }
    # One copy at a time may start BepInEx in a game folder (it rewrites BepInEx.cfg; a second copy starting at the same
    # moment crashes in a loop): a lock file per folder, held until the plugin has loaded. Works across parallel jobs.
    $lock = Join-Path $script:GameDir "BepInEx\.mp-starting"
    $held = $null
    $until = (Get-Date).AddSeconds(120)
    while (-not $held -and (Get-Date) -lt $until) {
        try { $held = [System.IO.File]::Open($lock, 'CreateNew', 'Write', 'None') }
        catch {
            # a lock left by a crashed start goes stale after a minute
            $age = try { ((Get-Date) - (Get-Item $lock -ErrorAction Stop).LastWriteTime).TotalSeconds } catch { 0 }
            if ($age -gt 60) { Remove-Item $lock -Force -ErrorAction SilentlyContinue }
            Start-Sleep -Milliseconds 300
        }
    }
    try {
        # stdout gets Mono's thread dumps when the watchdog sees the game hang
        $p = Start-Process -FilePath (Join-Path $script:GameDir "Emberville.exe") -ArgumentList $all -PassThru `
            -RedirectStandardOutput (Join-Path $OutDir "stdout.txt") -RedirectStandardError (Join-Path $OutDir "stderr.txt")
        $inst = [pscustomobject]@{ Tag = $Tag; Process = $p; Log = $log; Out = $OutDir }
        Wait-Loaded $inst
    }
    finally {
        if ($held) { $held.Dispose(); Remove-Item $lock -Force -ErrorAction SilentlyContinue }
    }
    $inst
}

# Two copies of one game folder can't start BepInEx at the same moment (both rewrite BepInEx.cfg; the loser crashes in
# a loop). Waits until this copy's plugin has loaded (its mp.log exists), so the next one can start safely.
function Wait-Loaded {
    param([Parameter(Mandatory)][object]$Instance, [int]$TimeoutSec = 45)
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    $mpLog = Join-Path $Instance.Out "mp.log"
    while ((Get-Date) -lt $deadline -and -not (Test-Path $mpLog) -and -not $Instance.Process.HasExited) { Start-Sleep -Milliseconds 100 }
    Start-Sleep -Milliseconds 300   # (mp.log comes after BepInEx has written its config; a little margin)
}

function Wait-Instances {
    param([object[]]$Instances, [int]$TimeoutSec = 180)
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    foreach ($i in $Instances) {
        $left = [int]([Math]::Max(1, ($deadline - (Get-Date)).TotalMilliseconds))
        if (-not $i.Process.WaitForExit($left)) {
            Write-Host "[$($i.Tag)] TIMEOUT - killing"
            Stop-Process -Id $i.Process.Id -Force -ErrorAction SilentlyContinue
        }
    }
}

function Show-Results {
    param([object[]]$Instances, [switch]$Full, [int]$Expected = 1)
    $ok = $true
    foreach ($i in $Instances) {
        # mp.log (written by the mod) has everything in both windowed and headless runs; player.log is Unity's.
        $mpLog = Join-Path $i.Out "mp.log"
        $src = if (Test-Path $mpLog) { $mpLog } else { $i.Log }
        $lines = if (Test-Path $src) { Get-Content $src } else { @() }
        $pattern = if ($Full) { "\[MP\]|\[dev\]|\[net\]|\[scene\]|Exception|NullReference" } else { "\[MP\]|Exception" }
        # every [MP] line (results must never be cut off), but only the first few errors (a flood would drown them)
        $lines | Where-Object { $_ -match $pattern -and $_ -notmatch "Exception|NullReference" } | ForEach-Object { "[$($i.Tag)] $_" }
        $lines | Where-Object { $_ -match "Exception|NullReference" } | Select-Object -First 30 | ForEach-Object { "[$($i.Tag)] $_" }
        # one RESULT line per scenario (a chain has several); every one must pass
        $results = @($lines | Where-Object { $_ -match "\[MP\] RESULT" })
        if ($results.Count -lt $Expected -or ($results | Where-Object { $_ -notmatch "RESULT PASS" })) { $ok = $false }
    }
    if ($ok) { "ALL PASS" } else { "FAILED" }
}
