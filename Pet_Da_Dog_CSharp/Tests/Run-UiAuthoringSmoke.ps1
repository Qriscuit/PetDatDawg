param(
    [string]$GodotPath = 'W:\Godot_v4.6.3-stable_mono_win64\Godot_v4.6.3-stable_mono_win64_console.exe',
    [switch]$SkipBuild,
    [switch]$SkipEditorProbe
)

$ErrorActionPreference = 'Stop'
$uiProjectPath = Split-Path -Parent $PSScriptRoot
$uiOldSteam = $env:PDD_DISABLE_STEAM
$uiOldTray = $env:PDD_DISABLE_TRAY
$uiOldAppData = $env:APPDATA
$uiOldLocalAppData = $env:LOCALAPPDATA
$uiOldTemp = $env:TEMP
$uiOldTmp = $env:TMP

function Invoke-UiGodot {
    param([string[]]$Arguments, [string]$Sentinel)
    $ErrorActionPreference = 'Continue'
    $uiOutput = & $GodotPath @Arguments 2>&1
    $uiExitCode = $LASTEXITCODE
    $ErrorActionPreference = 'Stop'
    $uiOutput | Write-Output
    $uiErrors = $uiOutput -match '^(?:SCRIPT ERROR:|ERROR: (?!Failed to read the root certificate store\.))'
    if ($uiExitCode -ne 0 -or $uiErrors -or ($Sentinel -and -not ($uiOutput -match $Sentinel))) {
        throw "UI authoring check failed (exit $uiExitCode, expected $Sentinel)."
    }
}

function Get-UiPreferenceSnapshot {
    $uiHashes = @{}
    foreach ($uiFile in Get-ChildItem -LiteralPath $env:APPDATA, $env:LOCALAPPDATA -Recurse -File -Filter '*.cfg') {
        $uiHashes[$uiFile.FullName] = (Get-FileHash -LiteralPath $uiFile.FullName -Algorithm SHA256).Hash
    }
    return $uiHashes
}

try {
    $env:PDD_DISABLE_STEAM = '1'
    $env:PDD_DISABLE_TRAY = '1'
    $env:APPDATA = Join-Path $uiProjectPath '.godot\ui-authoring-smoke-home\Roaming'
    $env:LOCALAPPDATA = Join-Path $uiProjectPath '.godot\ui-authoring-smoke-home\Local'
    $env:TEMP = Join-Path $uiProjectPath '.godot\ui-authoring-smoke-home\Temp'
    $env:TMP = $env:TEMP
    New-Item -ItemType Directory -Force -Path $env:APPDATA, $env:LOCALAPPDATA, $env:TEMP | Out-Null
    if (-not $SkipBuild) {
        dotnet build (Join-Path $uiProjectPath 'PetDaDogCSharp.csproj')
        if ($LASTEXITCODE -ne 0) { throw "Client build failed ($LASTEXITCODE)." }
        Invoke-UiGodot -Arguments @('--headless', '--editor', '--path', $uiProjectPath, '--import', '--quit', '--no-header')
    }
    if (-not $SkipEditorProbe) {
        $uiBefore = Get-UiPreferenceSnapshot
        Invoke-UiGodot -Arguments @('--headless', '--editor', '--path', $uiProjectPath,
            'res://Tests/UiAuthoringSmokeTest.tscn', '--quit-after', '300', '--no-header', '--', '--ui-authoring-editor-check') -Sentinel 'UI_AUTHORING_EDITOR_PASS:'
        $uiAfter = Get-UiPreferenceSnapshot
        if ($uiBefore.Count -ne $uiAfter.Count) { throw 'Editor preview wrote gameplay preferences.' }
        foreach ($uiKey in $uiBefore.Keys) {
            if (-not $uiAfter.ContainsKey($uiKey) -or $uiBefore[$uiKey] -ne $uiAfter[$uiKey]) { throw "Editor preview changed a preference file: $uiKey" }
        }
    }
    Invoke-UiGodot -Arguments @('--path', $uiProjectPath, '--scene', 'res://Tests/UiAuthoringSmokeTest.tscn',
        '--quit-after', '900', '--no-header') -Sentinel 'UI_AUTHORING_SMOKE_PASS:'
}
finally {
    $env:PDD_DISABLE_STEAM = $uiOldSteam
    $env:PDD_DISABLE_TRAY = $uiOldTray
    $env:APPDATA = $uiOldAppData
    $env:LOCALAPPDATA = $uiOldLocalAppData
    $env:TEMP = $uiOldTemp
    $env:TMP = $uiOldTmp
}
