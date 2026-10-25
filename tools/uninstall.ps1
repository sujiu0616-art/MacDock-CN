# Removes MacDock for the current user and gives the Windows taskbar back.
# Keeps %APPDATA%\MacDock\config.json so a reinstall finds the same apps and settings.
param([switch]$Purge, [switch]$RestoreNexusAutostart)

$ErrorActionPreference = "Stop"
$home2 = Join-Path $env:LOCALAPPDATA "MacDock"
$dst = Join-Path $home2 "app"
$exe = Join-Path $dst "MacDock.exe"

# The build is framework dependent: without DOTNET_ROOT the host shows a "requires .NET 9" dialog and
# waits forever, so every command line below carries the runtime this installation uses.
$runtime = $null
foreach ($candidate in @((Join-Path $home2 "runtime"), (Join-Path $env:USERPROFILE ".dotnet9"))) {
    if (Test-Path (Join-Path $candidate "shared\Microsoft.WindowsDesktop.App")) { $runtime = $candidate; break }
}

function Invoke-MacDock([string]$arguments, [int]$waitSeconds = 20) {
    if (-not (Test-Path $exe)) { return }
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $exe
    $psi.Arguments = $arguments
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true
    if ($runtime) { $psi.EnvironmentVariables["DOTNET_ROOT"] = $runtime }
    $p = [System.Diagnostics.Process]::Start($psi)
    if (-not $p.WaitForExit($waitSeconds * 1000)) { "   (!) $arguments did not finish in $waitSeconds s" }
}

if (Test-Path $exe) {
    "== restore the taskbar and stop the dock"
    Invoke-MacDock "--uninstall" 30    # quits the dock, restores the taskbar, drops the autostart entries
}

Get-Process MacDock, MacDockGuard -ErrorAction SilentlyContinue | Stop-Process -Force
if (Test-Path $exe) { Invoke-MacDock "--restore-taskbar" 20 }
Start-Sleep -Milliseconds 800

"== remove autostart entries and shortcuts"
foreach ($key in @("HKCU:\Software\Microsoft\Windows\CurrentVersion\Run",
                   "HKCU:\Software\Microsoft\Windows\CurrentVersion\RunOnce",
                   "HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run")) {
    foreach ($value in @("MacDock", "MacDockRestoreTaskbar")) {
        Remove-ItemProperty -Path $key -Name $value -ErrorAction SilentlyContinue
    }
}
# PS 5.1 turns a native tool''s stderr into a terminating error under "Stop": relax it for this call.
$SavedEap = $ErrorActionPreference
$ErrorActionPreference = "Continue"
if (Test-Path (Join-Path $env:WINDIR "System32\Tasks\MacDock")) { schtasks.exe /Delete /TN "MacDock" /F | Out-Null }
$ErrorActionPreference = $SavedEap
foreach ($lnk in @((Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs\MacDock.lnk"),
                   (Join-Path ([Environment]::GetFolderPath("Desktop")) "MacDock.lnk"))) {
    Remove-Item $lnk -ErrorAction SilentlyContinue
}
Remove-Item (Join-Path $home2 "MacDock.vbs") -ErrorAction SilentlyContinue

if ($RestoreNexusAutostart) {
    "== re-enable the Winstep Nexus sign-in task"
    $SavedEap = $ErrorActionPreference
$ErrorActionPreference = "Continue"
schtasks.exe /Change /TN "Nexus AutoStart" /ENABLE | Out-Null
$ErrorActionPreference = $SavedEap
}

if ($Purge) {
    "== delete $home2"
    Remove-Item $home2 -Recurse -Force -ErrorAction SilentlyContinue
}

$tray = @'
using System;using System.Runtime.InteropServices;
public class TrayProbe {
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern IntPtr FindWindowEx(IntPtr p, IntPtr a, string c, string w);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    public static bool AnyVisible() {
        var h = IntPtr.Zero;
        while ((h = FindWindowEx(IntPtr.Zero, h, "Shell_TrayWnd", null)) != IntPtr.Zero)
            if (IsWindowVisible(h)) return true;
        return false;
    }
}
'@
Add-Type -TypeDefinition $tray -Language CSharp -ErrorAction SilentlyContinue
"Windows taskbar back: $([TrayProbe]::AnyVisible())"
"done. Windows taskbar restored; config kept in $env:APPDATA\MacDock"
