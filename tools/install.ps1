# Installs MacDock for the current user (no administrator rights needed).
#   1. copies the framework-dependent build to %LOCALAPPDATA%\MacDock\app
#   2. makes sure a .NET 9 Desktop Runtime is reachable (reuses %USERPROFILE%\.dotnet9, else downloads one)
#   3. writes MacDock.vbs, the silent launcher that sets DOTNET_ROOT and starts the app
#   4. writes a config with the pinned apps from tools\pinned.json (only if there is none yet)
#   5. shortcuts in the Start menu and on the desktop
#   6. starts with Windows (Run key + sign-in scheduled task, both under HKCU / the current user)
#   7. takes the place of the Windows taskbar and stops Winstep Nexus from starting at sign-in
#
# This file stays ASCII: Windows PowerShell 5.1 reads a BOM-less .ps1 as ANSI, so non-ASCII text
# lives in tools\pinned.json (read explicitly as UTF-8) instead of in here.
param(
    [switch]$NoStart,
    [switch]$KeepNexus
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$src = Join-Path $root "dist\MacDock"
$home2 = Join-Path $env:LOCALAPPDATA "MacDock"
$dst = Join-Path $home2 "app"

if (-not (Test-Path (Join-Path $src "MacDock.exe"))) { throw "run tools\publish.ps1 first ($src is empty)" }

"== copy to $dst"
Get-Process MacDock, MacDockGuard -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500
New-Item -ItemType Directory -Force -Path $dst, $home2 | Out-Null
robocopy $src $dst /MIR /NFL /NDL /NJH /NJS /NP | Out-Null
if ($LASTEXITCODE -ge 8) { throw "robocopy failed: $LASTEXITCODE" }

"== .NET 9 desktop runtime"
$runtime = $null
$candidates = @((Join-Path $env:USERPROFILE ".dotnet9"), (Join-Path $home2 "runtime"))
foreach ($candidate in $candidates) {
    if (Test-Path (Join-Path $candidate "shared\Microsoft.WindowsDesktop.App")) { $runtime = $candidate; break }
}
if (-not $runtime) {
    $runtime = Join-Path $home2 "runtime"
    "   downloading a user-scoped runtime to $runtime"
    $script = Join-Path $PSScriptRoot "dotnet-install.ps1"
    if (-not (Test-Path $script)) { Invoke-WebRequest -Uri "https://dot.net/v1/dotnet-install.ps1" -OutFile $script -TimeoutSec 60 }
    & $script -Runtime windowsdesktop -Channel 9.0 -InstallDir $runtime -NoPath
    if (-not (Test-Path (Join-Path $runtime "shared\Microsoft.WindowsDesktop.App"))) { throw "runtime install failed" }
}
"   using $runtime"

"== silent launcher"
$launcher = Join-Path $home2 "MacDock.vbs"
$vbs = @"
' Starts MacDock with the .NET 9 runtime this installation uses. Written by tools\install.ps1.
Option Explicit
Dim shell, env, exe, args, i
Set shell = CreateObject("WScript.Shell")
Set env = shell.Environment("PROCESS")
env("DOTNET_ROOT") = "$runtime"
exe = "$dst\MacDock.exe"
args = ""
For i = 0 To WScript.Arguments.Count - 1
  args = args & " " & Chr(34) & WScript.Arguments(i) & Chr(34)
Next
' 两段式启动：先把 Windows 任务栏收掉（几百毫秒，不加载 WPF），再起 Dock。
shell.Run Chr(34) & exe & Chr(34) & " --hide-taskbar", 0, True
shell.Run Chr(34) & exe & Chr(34) & args, 0, False
"@
[System.IO.File]::WriteAllText($launcher, $vbs, (New-Object System.Text.ASCIIEncoding))
"   $launcher"

$exe = Join-Path $dst "MacDock.exe"
$wscript = Join-Path (Join-Path $env:SystemRoot "System32") "wscript.exe"
$cfgDir = Join-Path $env:APPDATA "MacDock"
$cfgFile = Join-Path $cfgDir "config.json"
New-Item -ItemType Directory -Force -Path $cfgDir | Out-Null

if (-not (Test-Path $cfgFile)) {
    "== write default config from tools\pinned.json"
    $seed = Get-Content (Join-Path $PSScriptRoot "pinned.json") -Raw -Encoding UTF8 | ConvertFrom-Json
    $pinned = @()
    foreach ($item in $seed.pinned) {
        if (Test-Path $item.Target) { $pinned += @{ Name = $item.Name; Target = $item.Target } }
    }
    $folders = @()
    foreach ($f in $seed.folders) {
        $path = [Environment]::ExpandEnvironmentVariables($f.Target)
        if (Test-Path $path) { $folders += @{ Name = $f.Name; Target = $path } }
    }
    $config = [ordered]@{
        Version  = 2
        Settings = [ordered]@{
            IconSize = 56.0; Magnification = $true; MagnifiedSize = 96.0; AutoHide = $false; Edge = "Bottom"
            ReplaceTaskbar = $true; TaskbarHotkey = "Ctrl+Alt+Shift+B"; StartWithWindows = $true
            NumberShortcuts = $true; MenuBar = $true; Language = "zh-CN"; Genie = $true
            MenuBarOnAllDisplays = $true; Display = ""; FollowPointer = $true
        }
        Items    = $pinned
        Folders  = $folders
    }
    [System.IO.File]::WriteAllText($cfgFile, ($config | ConvertTo-Json -Depth 6), (New-Object System.Text.UTF8Encoding($false)))
    "   pinned apps: $($pinned.Count)"
}

"== shortcuts"
$shell = New-Object -ComObject WScript.Shell
foreach ($lnkDir in @((Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs"), [Environment]::GetFolderPath("Desktop"))) {
    $lnk = $shell.CreateShortcut((Join-Path $lnkDir "MacDock.lnk"))
    $lnk.TargetPath = $wscript
    $lnk.Arguments = "`"$launcher`""
    $lnk.WorkingDirectory = $home2
    $lnk.IconLocation = "$exe,0"
    $lnk.Description = "macOS style taskbar: dock and menu bar"
    $lnk.Save()
}

"== autostart (Run key + sign-in task, launcher aware) and taskbar takeover"
$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = $exe
$psi.Arguments = "--set-autostart on"
$psi.UseShellExecute = $false
$psi.CreateNoWindow = $true
$psi.EnvironmentVariables["DOTNET_ROOT"] = $runtime
[System.Diagnostics.Process]::Start($psi) | Wait-Process -Timeout 40

if (-not $KeepNexus) {
    "== stop Winstep Nexus and disable its sign-in task"
    Get-Process Nexus -ErrorAction SilentlyContinue | Stop-Process -Force
    schtasks.exe /Change /TN "Nexus AutoStart" /DISABLE 2>&1 | Out-Null
}

if (-not $NoStart) {
    "== start MacDock"
    Start-Process -FilePath $wscript -ArgumentList "`"$launcher`"" -WorkingDirectory $home2
    Start-Sleep -Seconds 5
}

foreach ($proc in @(Get-Process MacDock, MacDockGuard -ErrorAction SilentlyContinue)) { "   running: $($proc.ProcessName) ($($proc.Id))" }
$run = (Get-ItemProperty "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run" -ErrorAction SilentlyContinue).MacDock
"autostart: $run"
"installed. Quit and give the taskbar back with:  & '$PSScriptRoot\uninstall.ps1'"
