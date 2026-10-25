# Starts MacDock (Debug build) detached from this shell, with DOTNET_ROOT pointing at the
# user-scoped .NET 9 (the machine has no machine-wide .NET 9 Desktop Runtime).
param([string]$Exe = "", [switch]$KeepConfig)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
if (-not $Exe) { $Exe = Join-Path $root "app\bin\Debug\net9.0-windows\MacDock.exe" }
if (-not (Test-Path $Exe)) { throw "not built: $Exe" }

Get-Process MacDock, MacDockGuard -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 400

$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = $Exe
$psi.WorkingDirectory = Split-Path $Exe
$psi.UseShellExecute = $false
$psi.EnvironmentVariables["DOTNET_ROOT"] = "$env:USERPROFILE\.dotnet9"
$p = [System.Diagnostics.Process]::Start($psi)
Start-Sleep -Seconds 4
$alive = Get-Process -Id $p.Id -ErrorAction SilentlyContinue
if ($alive) { "MacDock running, pid=$($p.Id)" } else { "MacDock exited early, exit code=$($p.ExitCode)"; Get-Content "$env:APPDATA\MacDock\log.txt" -Tail 30 -ErrorAction SilentlyContinue }
