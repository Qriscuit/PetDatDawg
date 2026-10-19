param(
    [string]$GodotPath = 'W:\Godot_v4.6.3-stable_mono_win64\Godot_v4.6.3-stable_mono_win64_console.exe',
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
$overlayProjectPath = Split-Path -Parent $PSScriptRoot
$overlayVariables = @('PDD_DISABLE_STEAM', 'PDD_DISABLE_GLOBAL_INPUT', 'PDD_DISABLE_TRAY', 'APPDATA', 'LOCALAPPDATA', 'TEMP', 'TMP')
$overlayPrevious = @{}
foreach ($overlayVariable in $overlayVariables) { $overlayPrevious[$overlayVariable] = [Environment]::GetEnvironmentVariable($overlayVariable, 'Process') }
$overlayProfile = Join-Path $overlayProjectPath ('.godot\overlay-smoke-home\' + [Guid]::NewGuid().ToString('N'))
try {
    $env:PDD_DISABLE_STEAM = '1'
    $env:PDD_DISABLE_GLOBAL_INPUT = '1'
    $env:PDD_DISABLE_TRAY = '1'
    $env:APPDATA = Join-Path $overlayProfile 'Roaming'
    $env:LOCALAPPDATA = Join-Path $overlayProfile 'Local'
    $env:TEMP = Join-Path $overlayProfile 'Temp'
    $env:TMP = $env:TEMP
    New-Item -ItemType Directory -Force -Path $env:APPDATA, $env:LOCALAPPDATA, $env:TEMP | Out-Null
    if (-not $SkipBuild) {
        $ErrorActionPreference = 'Continue'
        & $GodotPath --headless --editor --path $overlayProjectPath --import --quit --no-header
        $ErrorActionPreference = 'Stop'
        if ($LASTEXITCODE -ne 0) { throw "Godot import failed ($LASTEXITCODE)." }
        dotnet build (Join-Path $overlayProjectPath 'PetDaDogCSharp.csproj')
        if ($LASTEXITCODE -ne 0) { throw "Client build failed ($LASTEXITCODE)." }
    }
    $ErrorActionPreference = 'Continue'
    $overlayOutput = & $GodotPath --path $overlayProjectPath --scene 'res://Tests/OverlayRenderingSmokeTest.tscn' --quit-after 900 --no-header 2>&1
    $overlayExit = $LASTEXITCODE
    $ErrorActionPreference = 'Stop'
    $overlayOutput | Write-Output
    $overlayErrors = $overlayOutput -match '^(?:SCRIPT ERROR:|ERROR: (?!Failed to read the root certificate store\.))'
    if ($overlayExit -ne 0 -or $overlayErrors -or -not ($overlayOutput -match 'OVERLAY_RENDERING_SMOKE_PASS:')) {
        throw "Overlay rendering smoke test did not pass (exit $overlayExit)."
    }
}
finally {
    foreach ($overlayVariable in $overlayVariables) { [Environment]::SetEnvironmentVariable($overlayVariable, $overlayPrevious[$overlayVariable], 'Process') }
}
