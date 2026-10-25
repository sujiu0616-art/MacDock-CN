# One verification cycle: build, start MacDock, grab the dock and the menu bar, stop it again.
# Everything in one call so two agents never leave the app running over each other.
param(
    [string]$Name = "verify",
    [switch]$SkipBuild,
    [string[]]$AppArgs = @()
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$lock = Join-Path $env:TEMP "macdock-verify.lock"

# Only one agent may run the dock at a time: the app is single-instance.
$handle = $null
for ($i = 0; $i -lt 60; $i++) {
    try {
        $handle = [System.IO.File]::Open($lock, "OpenOrCreate", "ReadWrite", "None")
        break
    } catch { Start-Sleep -Seconds 3 }
}
if (-not $handle) { throw "another verification cycle is running (lock: $lock)" }

try {
    # Stop first: the running exe locks bin\Debug\...\MacDock.exe and the copy would fail.
    Get-Process MacDock, MacDockGuard -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 600
    if (-not $SkipBuild) {
        & (Join-Path $PSScriptRoot "build.ps1") | Select-Object -Last 6
        if ($LASTEXITCODE -ne 0) { throw "build failed" }
    }
    $exe = Join-Path $root "app\bin\Debug\net9.0-windows\MacDock.exe"
    Get-Process MacDock, MacDockGuard -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 500

    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $exe
    if ($AppArgs.Count -gt 0) { $psi.Arguments = ($AppArgs -join " ") }
    $psi.WorkingDirectory = Split-Path $exe
    $psi.UseShellExecute = $false
    $psi.EnvironmentVariables["DOTNET_ROOT"] = "$env:USERPROFILE\.dotnet9"
    [System.Diagnostics.Process]::Start($psi) | Out-Null
    Start-Sleep -Seconds 5

    & (Join-Path $PSScriptRoot "shot.ps1") -Name "$Name-dock" -Region dock
    & (Join-Path $PSScriptRoot "shot.ps1") -Name "$Name-top" -Region top
    if ($AppArgs.Count -eq 0) { & (Join-Path $PSScriptRoot "shot.ps1") -Name "$Name-full" -Region full }
} finally {
    if ($handle) { $handle.Dispose() }
}
