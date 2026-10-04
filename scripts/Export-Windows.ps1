[CmdletBinding()]
param(
    [string]$GodotPath = 'W:\Godot_v4.6.3-stable_mono_win64\Godot_v4.6.3-stable_mono_win64_console.exe',
    [string]$TemplatesDirectory,
    [switch]$StageOnly
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$sourceProject = Join-Path $repoRoot 'Pet_Da_Dog_CSharp'
$cacheRoot = Join-Path $sourceProject '.godot'
$outputPath = Join-Path $repoRoot 'Builds'
$runRoot = Join-Path $cacheRoot ('windows-export/' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
$stageProject = Join-Path $runRoot 'project'
$packagePath = Join-Path $runRoot 'package'

function Assert-InRepository([string]$Path) {
    $fullPath = [IO.Path]::GetFullPath($Path)
    if (-not $fullPath.StartsWith($repoRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Build operation outside the repository refused: $fullPath"
    }
}

function Invoke-CheckedProcess([string]$Executable, [string[]]$Arguments, [string]$Name, [int]$TimeoutSeconds = 300, [string]$WorkingDirectory = $stageProject) {
    $stdout = Join-Path $runRoot "$Name.stdout.log"
    $stderr = Join-Path $runRoot "$Name.stderr.log"
    $quoted = $Arguments | ForEach-Object { '"' + $_.Replace('"', '\"') + '"' }
    $process = Start-Process -FilePath $Executable -ArgumentList $quoted -WorkingDirectory $WorkingDirectory -PassThru -WindowStyle Hidden -RedirectStandardOutput $stdout -RedirectStandardError $stderr
    if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
        $process.Kill()
        throw "$Name timed out. Logs: $runRoot"
    }
    $process.WaitForExit()
    $log = (Get-Content -LiteralPath $stdout -Raw -ErrorAction SilentlyContinue) + (Get-Content -LiteralPath $stderr -Raw -ErrorAction SilentlyContinue)
    if ($process.ExitCode -ne 0 -or $log -match '(?m)^\s*(?:SCRIPT ERROR:|ERROR:|ERROR [A-Z]+\d+|Build FAILED)') {
        $diagnostics = ($log -split '\r?\n' | Where-Object { $_ -match '^\s*(?:SCRIPT ERROR:|ERROR:|ERROR [A-Z]+\d+|Build FAILED)' } | Select-Object -First 25) -join [Environment]::NewLine
        throw "$Name failed (exit $($process.ExitCode)). Logs: $runRoot`n$diagnostics"
    }
    Write-Host "$Name passed."
    return $log
}

if (-not (Test-Path -LiteralPath $GodotPath -PathType Leaf)) { throw "Godot executable not found: $GodotPath" }
$version = (& $GodotPath --version | Out-String).Trim()
if ($LASTEXITCODE -ne 0 -or $version -notmatch '^4\.6\.3\.stable\.mono\.') {
    throw "This project requires Godot 4.6.3 Mono; found '$version'."
}
if (-not $TemplatesDirectory) {
    $candidates = @(
        (Join-Path $cacheRoot 'build-tools/templates-4.6.3'),
        (Join-Path $env:APPDATA 'Godot/export_templates/4.6.3.stable.mono')
    )
    $TemplatesDirectory = $candidates | Where-Object { Test-Path -LiteralPath (Join-Path $_ 'windows_release_x86_64.exe') } | Select-Object -First 1
}
if (-not $TemplatesDirectory) { throw 'Install the official Godot 4.6.3 Mono export templates or pass -TemplatesDirectory.' }
$TemplatesDirectory = [IO.Path]::GetFullPath($TemplatesDirectory)
$templateVersion = (Get-Content -LiteralPath (Join-Path $TemplatesDirectory 'version.txt') -Raw).Trim()
if ($templateVersion -ne '4.6.3.stable.mono') { throw "Incorrect template version: $templateVersion" }
foreach ($template in @('windows_release_x86_64.exe', 'windows_release_x86_64_console.exe')) {
    if (-not (Test-Path -LiteralPath (Join-Path $TemplatesDirectory $template) -PathType Leaf)) { throw "Missing export template: $template" }
}

Assert-InRepository $runRoot
New-Item -ItemType Directory -Force -Path $stageProject, $packagePath | Out-Null
Write-Host "Staging complete Windows export in $runRoot"
# Build an isolated snapshot so a failed export cannot mix new resources with old assemblies.
Get-ChildItem -LiteralPath $sourceProject -Force | Where-Object {
    $_.Name -notin @('.godot', '.git', '.vs', 'bin', 'obj', 'export_templates')
} | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $stageProject -Recurse -Force }
$presetPath = Join-Path $stageProject 'export_presets.cfg'
$preset = Get-Content -LiteralPath $presetPath -Raw
$releaseTemplate = (Join-Path $TemplatesDirectory 'windows_release_x86_64.exe').Replace('\', '/')
$preset = $preset -replace '(?m)^custom_template/release=.*$', ('custom_template/release="' + $releaseTemplate + '"')
Set-Content -LiteralPath $presetPath -Value $preset -Encoding utf8

$null = Invoke-CheckedProcess 'dotnet' @('build', (Join-Path $stageProject 'PetDaDogCSharp.csproj')) 'build-client'
# Godot initializes the project theme before first importing its textures. Bootstrap
# only this fresh staging copy without it, then validate the actual saved configuration.
$stagedConfigPath = Join-Path $stageProject 'project.godot'
$stagedConfig = [IO.File]::ReadAllText($stagedConfigPath)
try {
    [IO.File]::WriteAllText($stagedConfigPath, ($stagedConfig -replace '(?m)^theme/custom=.*\r?\n?', ''))
    $null = Invoke-CheckedProcess $GodotPath @('--headless', '--path', $stageProject, '--editor', '--import') 'import-textures'
} finally {
    [IO.File]::WriteAllText($stagedConfigPath, $stagedConfig)
}
$null = Invoke-CheckedProcess $GodotPath @('--headless', '--path', $stageProject, '--editor', '--import') 'import-resources'
$exportExe = Join-Path $packagePath 'PetDaDogCSharp.exe'
$null = Invoke-CheckedProcess $GodotPath @('--headless', '--path', $stageProject, '--export-release', 'Windows Desktop', $exportExe) 'export-windows'

$dataPath = Join-Path $packagePath 'data_PetDaDogCSharp_windows_x86_64'
foreach ($required in @('PetDaDogCSharp.exe', 'PetDaDogCSharp.pck', 'PetDaDogCSharp.console.exe')) {
    if (-not (Test-Path -LiteralPath (Join-Path $packagePath $required) -PathType Leaf)) { throw "Incomplete export: $required missing." }
}
foreach ($required in @('PetDaDogCSharp.dll', 'GodotSharp.dll', 'Steamworks.NET.dll', 'coreclr.dll', 'hostfxr.dll', 'hostpolicy.dll', 'steam_api64.dll')) {
    if (-not (Get-ChildItem -LiteralPath $dataPath -Recurse -File -Filter $required)) { throw "Incomplete managed/runtime export: $required missing." }
}
# This is only the public development App ID. Publisher keys belong exclusively to the backend.
if (Test-Path -LiteralPath (Join-Path $sourceProject 'steam_appid.txt')) {
    Copy-Item -LiteralPath (Join-Path $sourceProject 'steam_appid.txt') -Destination $packagePath
}

$savedEnvironment = @{}
foreach ($name in @('PDD_DISABLE_STEAM', 'PDD_DISABLE_TRAY', 'APPDATA', 'LOCALAPPDATA')) { $savedEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process') }
try {
    $env:PDD_DISABLE_STEAM = '1'
    $env:PDD_DISABLE_TRAY = '1'
    $env:APPDATA = Join-Path $runRoot 'smoke-profile/Roaming'
    $env:LOCALAPPDATA = Join-Path $runRoot 'smoke-profile/Local'
    New-Item -ItemType Directory -Force -Path $env:APPDATA, $env:LOCALAPPDATA | Out-Null
    # Official release templates disable --scene overrides; smoke the actual shipping entrypoint.
    $smokeLog = Invoke-CheckedProcess $exportExe @('--quit-after', '60', '--', '--status') 'smoke-export' 60 $packagePath
    if ($smokeLog -notmatch 'Godot Engine v4\.6\.3\.stable\.mono') { throw "Exported runtime did not report its expected version. Logs: $runRoot" }
} finally {
    foreach ($name in $savedEnvironment.Keys) { [Environment]::SetEnvironmentVariable($name, $savedEnvironment[$name], 'Process') }
}

if ($StageOnly) {
    Write-Host "Validated package ready: $packagePath"
    return
}
Assert-InRepository $outputPath
$backupPath = Join-Path $runRoot 'previous-Builds'
Assert-InRepository $backupPath
if (Test-Path -LiteralPath $outputPath) { Move-Item -LiteralPath $outputPath -Destination $backupPath }
try {
    Move-Item -LiteralPath $packagePath -Destination $outputPath
} catch {
    if (Test-Path -LiteralPath $backupPath) { Move-Item -LiteralPath $backupPath -Destination $outputPath }
    throw
}
Write-Host "Complete validated Windows package: $outputPath"
Write-Host "Export logs and previous package: $runRoot"
