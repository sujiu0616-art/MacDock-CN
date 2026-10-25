# Publishes MacDock into dist\MacDock.
#
# Framework dependent on purpose: a self-contained (or ReadyToRun) publish makes ManagedShell's
# notification-area service fail to initialise on this machine, which costs the tray icons. The
# app therefore runs on the .NET 9 Desktop Runtime and is started through MacDock.vbs, the silent
# launcher that tools\install.ps1 writes (it sets DOTNET_ROOT and then starts the exe).
param([string]$Configuration = "Release")

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$env:DOTNET_ROOT = "$env:USERPROFILE\.dotnet9"
$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"
$env:DOTNET_NOLOGO = "1"
$dotnet = "$env:USERPROFILE\.dotnet9\dotnet.exe"
$out = Join-Path $root "dist\MacDock"

# A previous self-contained publish leaves the whole runtime in here; publishing on top of it would
# ship a framework-dependent app next to a stale runtime.
if (Test-Path $out) { Remove-Item $out -Recurse -Force }

Push-Location (Join-Path $root "app")
try {
    & $dotnet publish MacDock.csproj -c $Configuration --self-contained false -o $out -v m 2>&1 | Select-Object -Last 8
    if ($LASTEXITCODE -ne 0) { throw "publish failed" }
} finally { Pop-Location }

# The guard is a copy of the same host: the csproj target only creates it inside bin/, so mirror it.
Copy-Item (Join-Path $out "MacDock.exe") (Join-Path $out "MacDockGuard.exe") -Force

$size = (Get-ChildItem $out -Recurse -File | Measure-Object -Property Length -Sum).Sum / 1MB
"published: $out ($([math]::Round($size,1)) MB, framework dependent)"
