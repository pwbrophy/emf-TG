<#
.SYNOPSIS
  Builds the Thundergeddon server for the GL-MT3000 router and installs it
  there over the network (Wi-Fi or Ethernet - no USB needed).

.DESCRIPTION
  Needs: this PC on the router's network, and SSH key login to the router
  set up once (see README.md). Then the game is at http://<router>:8088

.EXAMPLE
  .\deploy.ps1
  .\deploy.ps1 -Router 192.168.8.1
#>
param(
    [string]$Router = "192.168.8.1"
)

$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

# Terminals opened before Go was installed don't have it on PATH yet.
if (-not (Get-Command go -ErrorAction SilentlyContinue)) {
    $env:Path += ";C:\Program Files\Go\bin"
}

$target  = "root@$Router"
# BatchMode: fail instead of prompting for a password.
$sshOpts = @("-o", "BatchMode=yes", "-o", "ConnectTimeout=8")

function Fail([string]$msg) {
    Write-Host ""
    Write-Host "DEPLOY FAILED: $msg" -ForegroundColor Red
    exit 1
}

Write-Host "==> Checking SSH access to $Router"
& ssh @sshOpts $target "true"
if ($LASTEXITCODE -ne 0) {
    Fail "can't log in to $target without a password. Is this PC on the router's network, and is the SSH key set up? See README.md."
}

$commit = "nogit"
try {
    $c = & git rev-parse --short HEAD 2>$null
    if ($LASTEXITCODE -eq 0 -and $c) { $commit = $c }
} catch { }
$version = "$(Get-Date -Format 'yyyy-MM-dd.HHmm')-$commit"

Write-Host "==> Building $version for linux/arm64"
$env:GOOS = "linux"; $env:GOARCH = "arm64"; $env:CGO_ENABLED = "0"
try {
    & go build -trimpath -ldflags "-s -w -X main.version=$version" -o build/thundergeddon-linux-arm64 .
    if ($LASTEXITCODE -ne 0) { Fail "go build failed" }
}
finally {
    Remove-Item Env:GOOS, Env:GOARCH, Env:CGO_ENABLED -ErrorAction SilentlyContinue
}

# The tanks' hello token is the same file Unity uses. It is gitignored, so in a
# worktree it only exists in the main checkout: look there too.
$tokenFile = $null
$candidates = @("$PSScriptRoot\..\Unity\EMF_Server\ws_token.txt")
try {
    $commonDir = & git rev-parse --path-format=absolute --git-common-dir 2>$null
    if ($LASTEXITCODE -eq 0 -and $commonDir) {
        $candidates += Join-Path (Split-Path $commonDir) "Unity\EMF_Server\ws_token.txt"
    }
} catch { }
foreach ($c in $candidates) {
    if (Test-Path $c) { $tokenFile = (Resolve-Path $c).Path; break }
}

$files = @("build/thundergeddon-linux-arm64", "router/thundergeddon.init", "router/install.sh")
if ($tokenFile) {
    Write-Host "==> Tank token: $tokenFile"
    # Copied under a relative path: scp would read "F:\..." as host "F".
    Copy-Item $tokenFile build/ws_token.txt -Force
    $files += "build/ws_token.txt"
} else {
    Write-Host "==> No Unity\EMF_Server\ws_token.txt found: the router will accept any tank (as Unity does without it)"
}

Write-Host "==> Uploading"
& ssh @sshOpts $target "mkdir -p /tmp/tg-deploy"
if ($LASTEXITCODE -ne 0) { Fail "couldn't create upload folder on the router" }
# -O: the router's SSH server (dropbear) has no SFTP, so use classic scp.
& scp -O @sshOpts @files "${target}:/tmp/tg-deploy/"
$uploadExit = $LASTEXITCODE
Remove-Item build/ws_token.txt -ErrorAction SilentlyContinue
if ($uploadExit -ne 0) { Fail "upload failed" }

Write-Host "==> Installing and restarting"
& ssh @sshOpts $target 'tr -d ''\r'' < /tmp/tg-deploy/install.sh | sh'
if ($LASTEXITCODE -ne 0) { Fail "install on the router failed (see output above)" }

Write-Host ""
Write-Host "Done. Open http://${Router}:8088 on any device connected to the router." -ForegroundColor Green
