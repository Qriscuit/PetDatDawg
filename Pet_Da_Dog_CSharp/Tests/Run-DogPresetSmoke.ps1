param(
    [string]$GodotPath = 'W:\Godot_v4.6.3-stable_mono_win64\Godot_v4.6.3-stable_mono_win64_console.exe',
    [switch]$SkipBuild,
    [switch]$Headless
)

$ErrorActionPreference = 'Stop'
$presetProjectPath = Split-Path -Parent $PSScriptRoot
$presetSmokeHome = Join-Path $presetProjectPath ('.godot\dog-preset-smoke-home-' + [Guid]::NewGuid().ToString('N'))
$presetOldEnvironment = @{}
foreach ($presetVariable in 'PDD_DISABLE_STEAM', 'PDD_DISABLE_TRAY', 'APPDATA', 'LOCALAPPDATA', 'TEMP', 'TMP') {
    $presetOldEnvironment[$presetVariable] = [Environment]::GetEnvironmentVariable($presetVariable, 'Process')
}
try {
    $env:PDD_DISABLE_STEAM = '1'
    $env:PDD_DISABLE_TRAY = '1'
    $env:APPDATA = Join-Path $presetSmokeHome 'Roaming'
    $env:LOCALAPPDATA = Join-Path $presetSmokeHome 'Local'
    $env:TEMP = Join-Path $presetSmokeHome 'Temp'
    $env:TMP = $env:TEMP
    New-Item -ItemType Directory -Force -Path $env:APPDATA, $env:LOCALAPPDATA, $env:TEMP | Out-Null
    if (-not $SkipBuild) {
        $ErrorActionPreference = 'Continue'
        & $GodotPath --headless --editor --path $presetProjectPath --import --quit --no-header
        $ErrorActionPreference = 'Stop'
        if ($LASTEXITCODE -ne 0) { throw "Godot import failed ($LASTEXITCODE)." }
        dotnet build (Join-Path $presetProjectPath 'PetDaDogCSharp.csproj')
        if ($LASTEXITCODE -ne 0) { throw "Client build failed ($LASTEXITCODE)." }
    }
    $presetGodotArguments = @('--path', $presetProjectPath, '--scene', 'res://Tests/DogPresetSmokeTest.tscn', '--quit-after', '1800', '--no-header')
    if ($Headless) { $presetGodotArguments += '--headless' }
    $ErrorActionPreference = 'Continue'
    $presetOutput = & $GodotPath @presetGodotArguments 2>&1
    $presetExitCode = $LASTEXITCODE
    $ErrorActionPreference = 'Stop'
    $presetOutput | Write-Output
    $presetUnexpectedErrors = $presetOutput -match '^(?:SCRIPT ERROR:|ERROR: (?!Failed to read the root certificate store\.))'
    if ($presetExitCode -ne 0 -or $presetUnexpectedErrors -or -not ($presetOutput -match 'DOG_PRESET_SMOKE_PASS:')) {
        throw "Dog preset smoke test did not pass (exit $presetExitCode)."
    }
}
finally {
    foreach ($presetVariable in $presetOldEnvironment.Keys) {
        [Environment]::SetEnvironmentVariable($presetVariable, $presetOldEnvironment[$presetVariable], 'Process')
    }
}
