# builds MacDock with the user-scoped .NET 9 SDK (no admin needed).
# Serialized: several agents may build at the same time, MSBuild obj/ is not concurrent-safe.
param([string]$Configuration = "Debug", [switch]$Quiet)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$env:DOTNET_ROOT = "$env:USERPROFILE\.dotnet9"
$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"
$env:DOTNET_NOLOGO = "1"
# 不留常驻的编译服务器/节点：VBCSCompiler 一旦被拉起会占 130~260MB 且不会自己退出
$env:MSBUILDDISABLENODEREUSE = "1"
$env:UseSharedCompilation = "false"
$dotnet = "$env:USERPROFILE\.dotnet9\dotnet.exe"

$mutex = New-Object System.Threading.Mutex($false, "Global\MacDockBuild")
$owned = $false
try {
    $owned = $mutex.WaitOne([TimeSpan]::FromMinutes(10))
    if (-not $owned) { throw "build lock timeout" }
    Push-Location (Join-Path $root "app")
    try {
        $out = & $dotnet build -p:UseSharedCompilation=false MacDock.csproj -c $Configuration -v m 2>&1
        $code = $LASTEXITCODE
        if (-not $Quiet -or $code -ne 0) { $out | Select-Object -Last 30 }
        exit $code
    } finally { Pop-Location }
} finally {
    if ($owned) { $mutex.ReleaseMutex() }
    $mutex.Dispose()
}
