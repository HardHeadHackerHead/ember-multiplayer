# Runs one single-instance self-test: .\scripts\run-test.ps1 dump [-Profile a] [-Scene Town] [-Extra @("--mp-timescale=4")]
param(
    [Parameter(Mandatory)][string]$Scenario,
    [string]$Profile = "solo",
    [string]$Scene = "",
    [int]$TimeoutSec = 150,
    [string[]]$Extra = @(),
    [switch]$Full,
    [switch]$Headless,
    [switch]$NoAutostart,    # stay on the main menu (menu screenshots)
    [int]$Width = 640, [int]$Height = 360   # the window (screenshots for the README: 1280 x 720)
)
. (Join-Path $PSScriptRoot "launch.ps1")
$auto = if ($Scene) { "--mp-autostart=$Scene" } else { "--mp-autostart" }
$mp = @("--mp-test=$Scenario", "--mp-profile=$Profile", "--mp-mute", "--mp-quit=$TimeoutSec") + $(if ($NoAutostart) { @() } else { @($auto) }) + $Extra
$i = Start-Instance -Headless:$Headless -Tag $Scenario -MpArgs $mp -Width $Width -Height $Height
Wait-Instances @($i) -TimeoutSec ($TimeoutSec + 15)
Show-Results @($i) -Full:$Full
