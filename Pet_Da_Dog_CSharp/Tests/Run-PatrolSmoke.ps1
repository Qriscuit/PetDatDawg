param(
    [string]$GodotPath = 'W:\Godot_v4.6.3-stable_mono_win64\Godot_v4.6.3-stable_mono_win64_console.exe',
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
$patrolProjectPath = Split-Path -Parent $PSScriptRoot
$patrolOldSteam = $env:PDD_DISABLE_STEAM
$patrolOldTray = $env:PDD_DISABLE_TRAY
$patrolOldAppData = $env:APPDATA
$patrolOldLocalAppData = $env:LOCALAPPDATA
$patrolOldTemp = $env:TEMP
$patrolOldTmp = $env:TMP
try {
    $env:PDD_DISABLE_STEAM = '1'
    $env:PDD_DISABLE_TRAY = '1'
    $env:APPDATA = Join-Path $patrolProjectPath '.godot\patrol-smoke-home\Roaming'
    $env:LOCALAPPDATA = Join-Path $patrolProjectPath '.godot\patrol-smoke-home\Local'
    $env:TEMP = Join-Path $patrolProjectPath '.godot\patrol-smoke-home\Temp'
    $env:TMP = $env:TEMP
    New-Item -ItemType Directory -Force -Path $env:APPDATA, $env:LOCALAPPDATA, $env:TEMP | Out-Null
    if (-not $SkipBuild) {
        $ErrorActionPreference = 'Continue'
        & $GodotPath --headless --editor --path $patrolProjectPath --import --quit --no-header
        $ErrorActionPreference = 'Stop'
        if ($LASTEXITCODE -ne 0) { throw "Godot import failed ($LASTEXITCODE)." }
        dotnet build (Join-Path $patrolProjectPath 'PetDaDogCSharp.csproj')
        if ($LASTEXITCODE -ne 0) { throw "Client build failed ($LASTEXITCODE)." }
    }
    # Native tests require the desktop window; stderr certificate warnings do not decide success.
    $ErrorActionPreference = 'Continue'
    $patrolOutput = & $GodotPath --path $patrolProjectPath --scene 'res://Tests/PatrolSmokeTest.tscn' --quit-after 1800 --no-header 2>&1
    $patrolExitCode = $LASTEXITCODE
    $ErrorActionPreference = 'Stop'
    $patrolOutput | Write-Output
    $patrolUnexpectedErrors = $patrolOutput -match '^(?:SCRIPT ERROR:|ERROR: (?!Failed to read the root certificate store\.))'
    if ($patrolExitCode -ne 0 -or $patrolUnexpectedErrors -or -not ($patrolOutput -match 'PATROL_SMOKE_PASS:')) {
        throw "Patrol smoke test did not pass (exit $patrolExitCode)."
    }
}
finally {
    $env:PDD_DISABLE_STEAM = $patrolOldSteam
    $env:PDD_DISABLE_TRAY = $patrolOldTray
    $env:APPDATA = $patrolOldAppData
    $env:LOCALAPPDATA = $patrolOldLocalAppData
    $env:TEMP = $patrolOldTemp
    $env:TMP = $patrolOldTmp
}
