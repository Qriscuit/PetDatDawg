param(
    [string]$GodotPath = 'W:\Godot_v4.6.3-stable_mono_win64\Godot_v4.6.3-stable_mono_win64_console.exe',
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
$steamInventoryProjectPath = Split-Path -Parent $PSScriptRoot
$steamInventoryOldSteam = $env:PDD_DISABLE_STEAM
$steamInventoryOldTray = $env:PDD_DISABLE_TRAY
$steamInventoryOldAppData = $env:APPDATA
$steamInventoryOldLocalAppData = $env:LOCALAPPDATA
$steamInventoryOldTemp = $env:TEMP
$steamInventoryOldTmp = $env:TMP
try {
    $env:PDD_DISABLE_STEAM = '1'
    $env:PDD_DISABLE_TRAY = '1'
    $env:APPDATA = Join-Path $steamInventoryProjectPath '.godot\steamInventory-smoke-home\Roaming'
    $env:LOCALAPPDATA = Join-Path $steamInventoryProjectPath '.godot\steamInventory-smoke-home\Local'
    $env:TEMP = Join-Path $steamInventoryProjectPath '.godot\steamInventory-smoke-home\Temp'
    $env:TMP = $env:TEMP
    New-Item -ItemType Directory -Force -Path $env:APPDATA, $env:LOCALAPPDATA, $env:TEMP | Out-Null
    if (-not $SkipBuild) {
        $ErrorActionPreference = 'Continue'
        & $GodotPath --headless --editor --path $steamInventoryProjectPath --import --quit --no-header
        $ErrorActionPreference = 'Stop'
        if ($LASTEXITCODE -ne 0) { throw "Godot import failed ($LASTEXITCODE)." }
        dotnet build (Join-Path $steamInventoryProjectPath 'PetDaDogCSharp.csproj')
        if ($LASTEXITCODE -ne 0) { throw "Client build failed ($LASTEXITCODE)." }
    }
    # Godot can emit an OS certificate warning on stderr in sandboxed runs.
    # Use the explicit exit status and test sentinel to determine success.
    $ErrorActionPreference = 'Continue'
    $steamInventoryOutput = & $GodotPath --path $steamInventoryProjectPath --scene 'res://Tests/SteamInventorySmokeTest.tscn' --quit-after 600 --no-header 2>&1
    $steamInventoryExitCode = $LASTEXITCODE
    $ErrorActionPreference = 'Stop'
    $steamInventoryOutput | Write-Output
    $steamInventoryUnexpectedErrors = $steamInventoryOutput -match '^(?:SCRIPT ERROR:|ERROR: (?!Failed to read the root certificate store\.))'
    if ($steamInventoryExitCode -ne 0 -or $steamInventoryUnexpectedErrors -or -not ($steamInventoryOutput -match 'STEAM_INVENTORY_SMOKE_PASS:')) {
        throw "SteamInventory smoke test did not pass (exit $steamInventoryExitCode)."
    }
}
finally {
    $env:PDD_DISABLE_STEAM = $steamInventoryOldSteam
    $env:PDD_DISABLE_TRAY = $steamInventoryOldTray
    $env:APPDATA = $steamInventoryOldAppData
    $env:LOCALAPPDATA = $steamInventoryOldLocalAppData
    $env:TEMP = $steamInventoryOldTemp
    $env:TMP = $steamInventoryOldTmp
}
