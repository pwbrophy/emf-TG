<#
.SYNOPSIS
  Builds and runs the Thundergeddon server on this PC for testing.

.EXAMPLE
  .\run.ps1          # this PC only:  http://localhost:8088
  .\run.ps1 -Lan     # also reachable from phones on the same network
                     # (Windows Firewall will ask to allow it the first time)
#>
param(
    [switch]$Lan
)

$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

if (-not (Get-Command go -ErrorAction SilentlyContinue)) {
    $env:Path += ";C:\Program Files\Go\bin"
}

& go build -o build/thundergeddon.exe .
if ($LASTEXITCODE -ne 0) { exit 1 }

$addr = if ($Lan) { ":8088" } else { "127.0.0.1:8088" }
Write-Host "Open http://localhost:8088   (Ctrl+C to stop)" -ForegroundColor Green
& ./build/thundergeddon.exe -addr $addr
