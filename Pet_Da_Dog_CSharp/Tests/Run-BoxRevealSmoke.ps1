param(
    [string]$GodotPath = 'W:\Godot_v4.6.3-stable_mono_win64\Godot_v4.6.3-stable_mono_win64_console.exe',
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
$revealProject = Split-Path -Parent $PSScriptRoot
$revealVariables = @('PDD_DISABLE_STEAM', 'PDD_DISABLE_GLOBAL_INPUT', 'PDD_DISABLE_TRAY', 'APPDATA', 'LOCALAPPDATA', 'TEMP', 'TMP')
$revealPrevious = @{}
foreach ($revealVariable in $revealVariables) { $revealPrevious[$revealVariable] = [Environment]::GetEnvironmentVariable($revealVariable, 'Process') }
$revealProfile = Join-Path $revealProject ('.godot\box-reveal-smoke-home\' + [Guid]::NewGuid().ToString('N'))
try {
    $env:PDD_DISABLE_STEAM = '1'
    $env:PDD_DISABLE_GLOBAL_INPUT = '1'
    $env:PDD_DISABLE_TRAY = '1'
    $env:APPDATA = Join-Path $revealProfile 'Roaming'
    $env:LOCALAPPDATA = Join-Path $revealProfile 'Local'
    $env:TEMP = Join-Path $revealProfile 'Temp'
    $env:TMP = $env:TEMP
    New-Item -ItemType Directory -Force -Path $env:APPDATA, $env:LOCALAPPDATA, $env:TEMP | Out-Null
    if (-not $SkipBuild) {
        $ErrorActionPreference = 'Continue'
        & $GodotPath --headless --editor --path $revealProject --import --quit --no-header
        $ErrorActionPreference = 'Stop'
        if ($LASTEXITCODE -ne 0) { throw "Godot import failed ($LASTEXITCODE)." }
        dotnet build (Join-Path $revealProject 'PetDaDogCSharp.csproj')
        if ($LASTEXITCODE -ne 0) { throw "Client build failed ($LASTEXITCODE)." }
    }
    $ErrorActionPreference = 'Continue'
    $revealOutput = & $GodotPath --path $revealProject --scene 'res://Tests/BoxRevealSmokeTest.tscn' --quit-after 900 --no-header 2>&1
    $revealExit = $LASTEXITCODE
    $ErrorActionPreference = 'Stop'
    $revealOutput | Write-Output
    $revealErrors = $revealOutput -match '^(?:SCRIPT ERROR:|ERROR: (?!Failed to read the root certificate store\.))'
    if ($revealExit -ne 0 -or $revealErrors -or -not ($revealOutput -match 'BOX_REVEAL_SMOKE_PASS:')) {
        throw "Box reveal smoke did not pass (exit $revealExit)."
    }
}
finally {
    foreach ($revealVariable in $revealVariables) { [Environment]::SetEnvironmentVariable($revealVariable, $revealPrevious[$revealVariable], 'Process') }
}
