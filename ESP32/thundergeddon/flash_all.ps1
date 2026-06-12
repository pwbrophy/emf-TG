#Requires -Version 5.1
<#
.SYNOPSIS
    Build firmware once, then flash all Thundergeddon robots in parallel via OTA.
.DESCRIPTION
    Runs `pio run -e thundergeddon_ota` to compile (no upload), then calls
    espota.py directly for every robot simultaneously — avoiding the scons
    build-database conflict that breaks parallel `pio run --target upload`.

    To add a robot: uncomment its entry and fill in the IP.
    To exclude a robot: comment out its entry.
#>

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# ── Robot list ────────────────────────────────────────────────────────────────
$Robots = @(
    [pscustomobject]@{ Name = "Robot 1";  Ip = "192.168.86.101" }
    [pscustomobject]@{ Name = "Robot 2";  Ip = "192.168.86.102" }
    [pscustomobject]@{ Name = "Robot 3";  Ip = "192.168.86.103" }
    [pscustomobject]@{ Name = "Robot 4";  Ip = "192.168.86.104" }
    [pscustomobject]@{ Name = "Robot 5";  Ip = "192.168.86.105" }
    [pscustomobject]@{ Name = "Robot 6";  Ip = "192.168.86.106" }
    [pscustomobject]@{ Name = "Robot 7";  Ip = "192.168.86.107" }
    [pscustomobject]@{ Name = "Robot 8";  Ip = "192.168.86.108" }
    [pscustomobject]@{ Name = "Robot 9";  Ip = "192.168.86.109" }
    [pscustomobject]@{ Name = "Robot 10"; Ip = "192.168.86.110" }
)
$Auth     = "thunder123"
$OtaPort  = 3232
$BuildEnv = "thundergeddon_ota"
# ─────────────────────────────────────────────────────────────────────────────

# Prefer PlatformIO's bundled Python (always available after `pio` runs)
$PythonExe = if (Test-Path "$env:USERPROFILE\.platformio\penv\Scripts\python.exe") {
    "$env:USERPROFILE\.platformio\penv\Scripts\python.exe"
} else {
    "python"
}

# Find espota.py once at startup
$EspotaPath = Get-ChildItem "$env:USERPROFILE\.platformio\packages" -Filter "espota.py" `
              -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1 -ExpandProperty FullName
if (-not $EspotaPath) {
    Write-Error "espota.py not found under ~/.platformio/packages — run 'pio pkg update' first."
}

# ── Build ─────────────────────────────────────────────────────────────────────
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

# ── Upload in parallel ────────────────────────────────────────────────────────
Write-Host ""
Write-Host "=== Flashing $($Robots.Count) robot(s) in parallel ===" -ForegroundColor Cyan

$Jobs = [System.Collections.Generic.List[object]]::new()
foreach ($Robot in $Robots) {
    Write-Host "  -> $($Robot.Name)  ($($Robot.Ip))"
    $job = Start-Job -Name $Robot.Name -ScriptBlock {
        param($python, $espota, $firmware, $ip, $port, $auth)
        # Redirect stderr to stdout so PS5.1 doesn't wrap it in ErrorRecords
        $lines    = & $python $espota -i $ip -p $port -a $auth -f $firmware 2>&1 | ForEach-Object { "$_" }
        $exitCode = $LASTEXITCODE
        [pscustomobject]@{
            ExitCode = $exitCode
            Output   = ($lines -join "`n")
        }
    } -ArgumentList $PythonExe, $EspotaPath, $FirmwarePath, $Robot.Ip, $OtaPort, $Auth
    $Jobs.Add($job)
}

Write-Host ""
Write-Host "Waiting for uploads to complete..." -ForegroundColor DarkGray
$null = $Jobs | Wait-Job

# ── Results ───────────────────────────────────────────────────────────────────
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
