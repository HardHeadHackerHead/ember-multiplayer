# Runs a two-instance self-test on this PC: a host and a guest (127.0.0.1), each with its own save profile.
#   .\scripts\run-pair.ps1 presence [-Scene Town] [-Port 27115] [-Fresh]
param(
    [Parameter(Mandatory)][string]$Scenario,
    [string]$Scene = "Town",
    [int]$Port = 27115,
    [int]$TimeoutSec = 150,
    [string[]]$Extra = @(),
    [string]$Tag = "",
    [switch]$Fresh,
    [switch]$Lan,
    [switch]$Headless,
    [switch]$JoinLater,
    [int]$Guests = 1,
    [switch]$MenuFlow,     # no switches to host/join: the test clicks through the main menu itself      # more than one guest: a 3- or 4-player session    # the guest plays its own world first, then joins
    [switch]$SteamLobby,   # host makes a public Steam lobby, the guest finds it through Steam (traffic over Steam IP sockets)
    [ValidateSet("lan", "steamip")][string]$Transport = "lan",
    [switch]$Full,
    [string[]]$Names = @(),   # players' names, host first (README shots); default Hosty, Guesty, Guesty2...
    [int]$Width = 640, [int]$Height = 360   # every copy's window (README shots: 1280 x 720)
)
. (Join-Path $PSScriptRoot "launch.ps1")
# EMB_TAG / EMB_PORTBASE keep parallel workers' save profiles and ports apart.
if ($env:EMB_TAG) { $Tag = "$($env:EMB_TAG)$Tag" }
if ($env:EMB_PORTBASE -and -not $PSBoundParameters.ContainsKey("Port")) { $Port = [int]$env:EMB_PORTBASE }
if ($Fresh) {
    foreach ($p in @("host", "guest", "guest2", "guest3")) {
        Remove-Item -Recurse -Force "$env:USERPROFILE\AppData\LocalLow\CygnusCross\Emberville\Saves_$($Tag)$p" -ErrorAction SilentlyContinue
    }
}
# A comma list runs the scenarios one after another in the same copies (fresh world each time, no restart).
$Label = $Scenario -replace ",", "+"
if ($Label.Length -gt 60) { $Label = $Label.Substring(0, 60) }
$common = @("--mp-players=$($Guests + 1)", "--mp-transport=$Transport", "--mp-test=$Scenario", "--mp-autostart=$Scene", "--mp-mute", "--mp-skip-dialogue", "--mp-quit=$TimeoutSec") + @($Extra | Where-Object { $_ })
$hostArgs = @("--mp-host=$Port")
if ($MenuFlow) {
    $hostArgs = @("--mp-prepare")
    $common = $common | ForEach-Object { if ($_ -like "--mp-autostart*") { "--mp-autostart" } else { $_ } }
    $common += "--mp-prepare"
}
$guestJoin = $(if ($MenuFlow) { "--mp-prepare" } elseif ($Lan) { "--mp-join=lan" } elseif ($JoinLater) { "--mp-join-later=127.0.0.1:$Port" } else { "--mp-join=127.0.0.1:$Port" })
if ($SteamLobby) {
    $nonce = [guid]::NewGuid().ToString("N").Substring(0, 12)
    $Transport = "steamip"
    $hostArgs = @("--mp-host=$Port", "--mp-host-steam", "--mp-lobby-test=$nonce")
    $guestJoin = "--mp-join=lobby"
    $common = $common | ForEach-Object { if ($_ -like "--mp-transport=*") { "--mp-transport=steamip" } else { $_ } }
    $common += "--mp-lobby-test=$nonce"
}
$Names = @($Names | ForEach-Object { $_ -split "," } | Where-Object { $_ })
$nameOf = { param($i, $default) if ($i -lt $Names.Count) { $Names[$i] } else { $default } }
$h = Start-Instance -Headless:$Headless -Width $Width -Height $Height -Tag "$Label-host" -MpArgs (@("--mp-profile=$($Tag)host", "--mp-name=$(& $nameOf 0 'Hosty')") + $hostArgs + $common)
# BepInEx rewrites its config at startup; two copies starting at the same instant collide on it.
$all = @($h)
for ($n = 1; $n -le $Guests; $n++) {
    $suffix = if ($n -eq 1) { "" } else { "$n" }
    $all += Start-Instance -Headless:$Headless -Width $Width -Height $Height -Tag "$Label-guest$suffix" -MpArgs (@("--mp-profile=$($Tag)guest$suffix", "--mp-name=$(& $nameOf $n "Guesty$suffix")", $guestJoin, "--mp-lan-port=$Port") + $common)
}
Wait-Instances $all -TimeoutSec ($TimeoutSec + 20)
Show-Results $all -Full:$Full -Expected @($Scenario -split ",").Count
