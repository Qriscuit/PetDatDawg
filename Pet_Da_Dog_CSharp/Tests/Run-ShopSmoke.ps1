param(
    [string]$GodotPath = 'W:\Godot_v4.6.3-stable_mono_win64\Godot_v4.6.3-stable_mono_win64_console.exe',
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
$shopProjectPath = Split-Path -Parent $PSScriptRoot
$shopOldSteam = $env:PDD_DISABLE_STEAM
$shopOldGlobalInput = $env:PDD_DISABLE_GLOBAL_INPUT
$shopOldTray = $env:PDD_DISABLE_TRAY
$shopOldAppData = $env:APPDATA
$shopOldLocalAppData = $env:LOCALAPPDATA
$shopOldTemp = $env:TEMP
$shopOldTmp = $env:TMP
try {
    $env:PDD_DISABLE_STEAM = '1'
    $env:PDD_DISABLE_GLOBAL_INPUT = '1'
    $env:PDD_DISABLE_TRAY = '1'
    $env:APPDATA = Join-Path $shopProjectPath '.godot\shop-smoke-home\Roaming'
    $env:LOCALAPPDATA = Join-Path $shopProjectPath '.godot\shop-smoke-home\Local'
    $env:TEMP = Join-Path $shopProjectPath '.godot\shop-smoke-home\Temp'
    $env:TMP = $env:TEMP
    New-Item -ItemType Directory -Force -Path $env:APPDATA, $env:LOCALAPPDATA, $env:TEMP | Out-Null
    if (-not $SkipBuild) {
        $ErrorActionPreference = 'Continue'
        & $GodotPath --headless --editor --path $shopProjectPath --import --quit --no-header
        $ErrorActionPreference = 'Stop'
        if ($LASTEXITCODE -ne 0) { throw "Godot import failed ($LASTEXITCODE)." }
        dotnet build (Join-Path $shopProjectPath 'PetDaDogCSharp.csproj')
        if ($LASTEXITCODE -ne 0) { throw "Client build failed ($LASTEXITCODE)." }
    }
    # Godot can emit an OS certificate warning on stderr in sandboxed runs.
    # Use the explicit exit status and test sentinel to determine success.
    $ErrorActionPreference = 'Continue'
    $shopOutput = & $GodotPath --path $shopProjectPath --scene 'res://Tests/ShopSmokeTest.tscn' --quit-after 600 --no-header 2>&1
    $shopExitCode = $LASTEXITCODE
    $ErrorActionPreference = 'Stop'
    $shopOutput | Write-Output
    $shopUnexpectedErrors = $shopOutput -match '^(?:SCRIPT ERROR:|ERROR: (?!Failed to read the root certificate store\.))'
    if ($shopExitCode -ne 0 -or $shopUnexpectedErrors -or -not ($shopOutput -match 'SHOP_SMOKE_PASS:')) {
        throw "Shop smoke test did not pass (exit $shopExitCode)."
    }
}
finally {
    $env:PDD_DISABLE_STEAM = $shopOldSteam
    $env:PDD_DISABLE_GLOBAL_INPUT = $shopOldGlobalInput
    $env:PDD_DISABLE_TRAY = $shopOldTray
    $env:APPDATA = $shopOldAppData
    $env:LOCALAPPDATA = $shopOldLocalAppData
    $env:TEMP = $shopOldTemp
    $env:TMP = $shopOldTmp
}
