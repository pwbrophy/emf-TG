<#
.SYNOPSIS
  Fetches the hub's logs (hub log, event log, video log and a snapshot of its
  state) into a new folder under Logs\ at the top of the project, ready to be
  looked at. Run it in a break between games, as often as you like: the hub
  keeps its logs, so each fetch has everything since the logs last filled up.

.DESCRIPTION
  One download: http://<hub>:8088/api/logs.zip. Needs this PC on the hub's
  network; no SSH. The folder is named after the time, plus -Label if given.

  What's in it (see Server\README.md, "Logs"):
    events.jsonl   one line per thing that happened: games, shots, kills,
                   tanks and phones connecting and dropping, and every 30 s
                   each tank's memory and the hub's memory and processor use
    server.log     everything the hub printed
    video.csv      every tank's video and Wi-Fi figures, every 2 s
    *.1            the older half of each, once a log has filled up
    info.json, stats.json, tanks.json, settings.json   the hub's state now
    router-system-log.txt   the router's own log (Wi-Fi driver, kernel)
    crash-previous.txt      only if the hub crashed: its last words

.EXAMPLE
  .\fetch-logs.ps1
  .\fetch-logs.ps1 -Label "after game 6"
  .\fetch-logs.ps1 -Hub 192.168.86.27       # a hub running on a PC (run.ps1 -Lan)
#>
param(
    [string]$Hub = "192.168.8.1",
    [int]$Port = 8088,
    [string]$Label = ""
)

$ErrorActionPreference = "Stop"

# Logs\ at the top of the project (gitignored). In a Claude worktree the top
# of the project is the main checkout, so every session's fetches land in the
# same place.
$top = Split-Path $PSScriptRoot
try {
    $commonDir = & git -C $PSScriptRoot rev-parse --path-format=absolute --git-common-dir 2>$null
    if ($LASTEXITCODE -eq 0 -and $commonDir) { $top = Split-Path $commonDir }
} catch { }

$name = Get-Date -Format "yyyy-MM-dd_HHmmss"
if ($Label) { $name += "_" + ($Label -replace '[^\w\-]+', '-') }
$dir = Join-Path (Join-Path $top "Logs") $name
New-Item -ItemType Directory -Force $dir | Out-Null

$zip = Join-Path $dir "logs.zip"
Write-Host "==> Fetching http://${Hub}:${Port}/api/logs.zip"
try {
    Invoke-WebRequest -Uri "http://${Hub}:${Port}/api/logs.zip" -OutFile $zip -TimeoutSec 60 -UseBasicParsing
} catch {
    Remove-Item $dir -Recurse -Force -ErrorAction SilentlyContinue
    Write-Host "FETCH FAILED: $($_.Exception.Message)" -ForegroundColor Red
    Write-Host "Is this PC on the hub's network, and is the hub running a version with /api/logs.zip (deploy.ps1)?"
    exit 1
}
Expand-Archive -Path $zip -DestinationPath $dir -Force
Remove-Item $zip

# This PC's clock next to the hub's (info.json has the hub's): the hub's can
# be wrong when the router has no internet to set it from.
"PC time at fetch: $(Get-Date -Format 'yyyy-MM-ddTHH:mm:ss.fff')" | Out-File (Join-Path $dir "fetched.txt") -Encoding utf8

Write-Host ""
Get-ChildItem $dir | Sort-Object Name | ForEach-Object {
    "{0,10:N0} bytes  {1}" -f $_.Length, $_.Name
}
Write-Host ""
Write-Host "Saved to $dir" -ForegroundColor Green
