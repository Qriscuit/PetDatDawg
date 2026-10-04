param(
    [string]$GodotPath = 'W:\Godot_v4.6.3-stable_mono_win64\Godot_v4.6.3-stable_mono_win64_console.exe',
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
$accessoryProjectPath = Split-Path -Parent $PSScriptRoot
$accessoryOldSteam = $env:PDD_DISABLE_STEAM
$accessoryOldTray = $env:PDD_DISABLE_TRAY
$accessoryOldAppData = $env:APPDATA
$accessoryOldLocalAppData = $env:LOCALAPPDATA
$accessoryOldTemp = $env:TEMP
$accessoryOldTmp = $env:TMP
try {
    $env:PDD_DISABLE_STEAM = '1'
    $env:PDD_DISABLE_TRAY = '1'
    $env:APPDATA = Join-Path $accessoryProjectPath '.godot\accessory-smoke-home\Roaming'
    $env:LOCALAPPDATA = Join-Path $accessoryProjectPath '.godot\accessory-smoke-home\Local'
    $env:TEMP = Join-Path $accessoryProjectPath '.godot\accessory-smoke-home\Temp'
    $env:TMP = $env:TEMP
    New-Item -ItemType Directory -Force -Path $env:APPDATA, $env:LOCALAPPDATA, $env:TEMP | Out-Null
    if (-not $SkipBuild) {
        $ErrorActionPreference = 'Continue'
        & $GodotPath --headless --editor --path $accessoryProjectPath --import --quit --no-header
        $ErrorActionPreference = 'Stop'
        if ($LASTEXITCODE -ne 0) { throw "Godot import failed ($LASTEXITCODE)." }
        dotnet build (Join-Path $accessoryProjectPath 'PetDaDogCSharp.csproj')
        if ($LASTEXITCODE -ne 0) { throw "Client build failed ($LASTEXITCODE)." }
    }
    # Godot can emit an OS certificate warning on stderr in sandboxed runs.
    # Use the explicit exit status and test sentinel to determine success.
    $ErrorActionPreference = 'Continue'
    $accessoryOutput = & $GodotPath --path $accessoryProjectPath --scene 'res://Tests/AccessorySmokeTest.tscn' --quit-after 600 --no-header 2>&1
    $accessoryExitCode = $LASTEXITCODE
    $ErrorActionPreference = 'Stop'
    $accessoryOutput | Write-Output
    $accessoryUnexpectedErrors = $accessoryOutput -match '^(?:SCRIPT ERROR:|ERROR: (?!Failed to read the root certificate store\.))'
    if ($accessoryExitCode -ne 0 -or $accessoryUnexpectedErrors -or -not ($accessoryOutput -match 'ACCESSORY_SMOKE_PASS:')) {
        throw "Accessory smoke test did not pass (exit $accessoryExitCode)."
    }
}
finally {
    $env:PDD_DISABLE_STEAM = $accessoryOldSteam
    $env:PDD_DISABLE_TRAY = $accessoryOldTray
    $env:APPDATA = $accessoryOldAppData
    $env:LOCALAPPDATA = $accessoryOldLocalAppData
    $env:TEMP = $accessoryOldTemp
    $env:TMP = $accessoryOldTmp
}
