#Requires -Version 5.1
param(
    [int[]]$Only = @(),          # e.g. .\flash_all.ps1 1,2,7,8  -- omit to flash all
    [ValidateSet("BFG","TG")]
    [string]$Network = "BFG",    # BFG = desktop (192.168.86.x), TG = laptop (192.168.8.x)
    [string]$Auth = ""           # OTA auth override (e.g. transition flash after a password change);
                                 # default reads ota_password from secrets.ini
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# --- Robot lists -------------------------------------------------------------
# BFG network: desktop PC on 192.168.86.x
$BfgRobots = @(
    [pscustomobject]@{ Num = 1;  Name = "Desert-01";  Ip = "192.168.86.101" }
    [pscustomobject]@{ Num = 2;  Name = "Desert-02";  Ip = "192.168.86.102" }
    [pscustomobject]@{ Num = 3;  Name = "Desert-03";  Ip = "192.168.86.103" }
    [pscustomobject]@{ Num = 4;  Name = "Desert-04";  Ip = "192.168.86.104" }
    [pscustomobject]@{ Num = 5;  Name = "Desert-05";  Ip = "192.168.86.105" }
    [pscustomobject]@{ Num = 6;  Name = "Jungle-06";  Ip = "192.168.86.106" }
    [pscustomobject]@{ Num = 7;  Name = "Jungle-07";  Ip = "192.168.86.107" }
    [pscustomobject]@{ Num = 8;  Name = "Jungle-08";  Ip = "192.168.86.108" }
    [pscustomobject]@{ Num = 9;  Name = "Jungle-09";  Ip = "192.168.86.109" }
    [pscustomobject]@{ Num = 10; Name = "Jungle-10";  Ip = "192.168.86.110" }
)

# Thundergeddon network: laptop (HP, 192.168.8.100) on 192.168.8.x
$TgRobots = @(
    [pscustomobject]@{ Num = 1;  Name = "Desert-01";  Ip = "192.168.8.101" }
    [pscustomobject]@{ Num = 2;  Name = "Desert-02";  Ip = "192.168.8.102" }
    [pscustomobject]@{ Num = 3;  Name = "Desert-03";  Ip = "192.168.8.103" }
    [pscustomobject]@{ Num = 4;  Name = "Desert-04";  Ip = "192.168.8.104" }
    [pscustomobject]@{ Num = 5;  Name = "Desert-05";  Ip = "192.168.8.105" }
    [pscustomobject]@{ Num = 6;  Name = "Jungle-06";  Ip = "192.168.8.106" }
    [pscustomobject]@{ Num = 7;  Name = "Jungle-07";  Ip = "192.168.8.107" }
    [pscustomobject]@{ Num = 8;  Name = "Jungle-08";  Ip = "192.168.8.108" }
    [pscustomobject]@{ Num = 9;  Name = "Jungle-09";  Ip = "192.168.8.109" }
    [pscustomobject]@{ Num = 10; Name = "Jungle-10";  Ip = "192.168.8.110" }
)

$AllRobots = if ($Network -eq "TG") { $TgRobots } else { $BfgRobots }
Write-Host "Network: $Network  (subnet $(($AllRobots[0].Ip -replace '\.\d+$','')).*)" -ForegroundColor DarkCyan

$Robots = @(if ($Only.Count -gt 0) {
    $AllRobots | Where-Object { $Only -contains $_.Num }
} else {
    $AllRobots
})

if ($Robots.Count -eq 0) { Write-Error "No robots matched: $Only" }

# OTA password lives in secrets.ini (gitignored) — same value the new firmware bakes
# in from src/secrets.h. Use -Auth to override when the fleet still runs an older password.
if (-not $Auth) {
    $secretsIni = Join-Path $PSScriptRoot "secrets.ini"
    if (-not (Test-Path $secretsIni)) {
        Write-Error "secrets.ini not found — copy secrets.ini.template and set ota_password."
    }
    $match = Select-String -Path $secretsIni -Pattern '^\s*ota_password\s*=\s*(.+?)\s*$' | Select-Object -First 1
    if (-not $match) { Write-Error "ota_password not found in secrets.ini" }
    $Auth = $match.Matches[0].Groups[1].Value
}
$OtaPort  = 3232
$BuildEnv = "thundergeddon_ota"
# -----------------------------------------------------------------------------

# Prefer PlatformIO's bundled Python
$PythonExe = "python"
if (Test-Path "$env:USERPROFILE\.platformio\penv\Scripts\python.exe") {
    $PythonExe = "$env:USERPROFILE\.platformio\penv\Scripts\python.exe"
}

# Find espota.py
$EspotaPath = Get-ChildItem "$env:USERPROFILE\.platformio\packages" -Filter "espota.py" -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1 -ExpandProperty FullName
if (-not $EspotaPath) {
    Write-Error "espota.py not found under ~/.platformio/packages - run 'pio pkg update' first."
}

# --- Build -------------------------------------------------------------------
Write-Host ""
Write-Host "=== Building firmware ($BuildEnv) ===" -ForegroundColor Cyan
& pio run -e $BuildEnv
if ($LASTEXITCODE -ne 0) { Write-Error "Build failed (exit $LASTEXITCODE)." }

$FirmwarePath = Join-Path (Get-Location) ".pio\build\$BuildEnv\firmware.bin"
if (-not (Test-Path $FirmwarePath)) {
    Write-Error "Firmware not found: $FirmwarePath"
}
$FirmwarePath = (Resolve-Path $FirmwarePath).Path
Write-Host "Firmware: $FirmwarePath" -ForegroundColor Green

# --- Upload in parallel ------------------------------------------------------
Write-Host ""
Write-Host "=== Flashing $($Robots.Count) robot(s) in parallel ===" -ForegroundColor Cyan

$Jobs = [System.Collections.Generic.List[object]]::new()
foreach ($Robot in $Robots) {
    Write-Host "  -> $($Robot.Name)  ($($Robot.Ip))"
    $job = Start-Job -Name $Robot.Name -ScriptBlock {
        param($python, $espota, $firmware, $ip, $port, $auth)
        $lines = & $python $espota -i $ip -p $port -a $auth -f $firmware 2>&1 | ForEach-Object { "$_" }
        [pscustomobject]@{
            ExitCode = $LASTEXITCODE
            Output   = ($lines -join "`n")
        }
    } -ArgumentList $PythonExe, $EspotaPath, $FirmwarePath, $Robot.Ip, $OtaPort, $Auth
    $Jobs.Add($job)
}

Write-Host ""
Write-Host "Waiting for uploads to complete..." -ForegroundColor DarkGray
$null = $Jobs | Wait-Job

# --- Results -----------------------------------------------------------------
Write-Host ""
Write-Host "=== Results ===" -ForegroundColor Cyan
$successCount = 0
$totalCount   = $Jobs.Count
foreach ($job in $Jobs) {
    $result  = Receive-Job $job
    $success = ($result.ExitCode -eq 0) -or ($result.Output -match "Result: OK")
    if ($success) {
        Write-Host ("  OK   " + $job.Name) -ForegroundColor Green
        $successCount++
    } else {
        Write-Host ("  FAIL " + $job.Name) -ForegroundColor Red
        if ($result.Output) {
            $result.Output -split "`n" | ForEach-Object { Write-Host "       $_" -ForegroundColor DarkGray }
        }
    }
    Remove-Job $job
}

Write-Host ""
$colour = if ($successCount -eq $totalCount) { "Green" } else { "Yellow" }
Write-Host "$successCount / $totalCount robots flashed successfully" -ForegroundColor $colour
if ($successCount -lt $totalCount) { exit 1 }
