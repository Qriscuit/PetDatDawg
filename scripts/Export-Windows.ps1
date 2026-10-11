[CmdletBinding()]
param(
    [string]$GodotPath = $env:PDD_GODOT_PATH,
    [string]$TemplatesDirectory = $env:PDD_GODOT_TEMPLATES,
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

function Copy-ClientSource([string]$Source, [string]$Destination) {
    New-Item -ItemType Directory -Force -Path $Destination | Out-Null
    foreach ($item in Get-ChildItem -LiteralPath $Source -Force) {
        if ($item.PSIsContainer) {
            if ($item.Name -notin @('.godot', '.git', '.vs', 'bin', 'obj', 'export_templates', 'node_modules')) {
                Copy-ClientSource $item.FullName (Join-Path $Destination $item.Name)
            }
        } elseif ($item.Name -notmatch '^(?:\.env(?:\..*)?|\.dev\.vars(?:\..*)?|export_credentials\.cfg)$' -and
                  $item.Extension -notin @('.pem', '.key', '.pfx', '.p12')) {
            Copy-Item -LiteralPath $item.FullName -Destination $Destination -Force
        }
    }
}

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
    # Windows PowerShell can lose ExitCode for a short-lived child unless its
    # native handle is acquired before waiting for completion.
    $null = $process.Handle
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

if (-not $GodotPath) {
    $GodotPath = 'W:\Godot_v4.6.3-stable_mono_win64\Godot_v4.6.3-stable_mono_win64_console.exe'
}
if (-not (Test-Path -LiteralPath $GodotPath -PathType Leaf)) {
    throw "Godot 4.6.3 Mono executable not found: $GodotPath. Pass -GodotPath or set PDD_GODOT_PATH."
}
$GodotPath = [IO.Path]::GetFullPath($GodotPath)
$dotnet = Get-Command 'dotnet' -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $dotnet) { throw 'The .NET 8 SDK is required. Install it and reopen the build window.' }
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
# Only client files are staged; local secret and credential files never enter the package.
Copy-ClientSource $sourceProject $stageProject
$stagedConfigPath = Join-Path $stageProject 'project.godot'
$stagedConfig = [IO.File]::ReadAllText($stagedConfigPath)
$backendMatch = [regex]::Match($stagedConfig, '(?ms)^\[pdd\]\s*\r?\n(?:(?!^\[).)*?^backend_url="([^"\r\n]+)"')
$backendUri = $null
if (-not $backendMatch.Success -or
    -not [Uri]::TryCreate($backendMatch.Groups[1].Value, [UriKind]::Absolute, [ref]$backendUri) -or
    $backendUri.Scheme -ne 'https' -or $backendUri.UserInfo) {
    throw 'Set pdd/backend_url in project.godot to the public HTTPS Worker address before exporting.'
}
$steamAppId = '4817200'
$sourceAppIdPath = Join-Path $stageProject 'steam_appid.txt'
if (Test-Path -LiteralPath $sourceAppIdPath -PathType Leaf) {
    $steamAppId = (Get-Content -LiteralPath $sourceAppIdPath -Raw).Trim()
}
if ($steamAppId -notmatch '^[1-9][0-9]*$' -or $steamAppId -eq '480') {
    throw 'Use the real public Steam AppID in steam_appid.txt before exporting (this app uses 4817200).'
}
Write-Host "Packaged Worker: $($backendUri.AbsoluteUri)"
Write-Host "Packaged Steam AppID: $steamAppId"
$presetPath = Join-Path $stageProject 'export_presets.cfg'
$preset = Get-Content -LiteralPath $presetPath -Raw
$releaseTemplate = (Join-Path $TemplatesDirectory 'windows_release_x86_64.exe').Replace('\', '/')
$preset = $preset -replace '(?m)^custom_template/release=.*$', ('custom_template/release="' + $releaseTemplate + '"')
# Godot's preset parser requires UTF-8 without the Windows PowerShell BOM.
[IO.File]::WriteAllText($presetPath, $preset)

# The editor resolves C# autoloads from Debug output during the import pass.
$null = Invoke-CheckedProcess $dotnet.Source @('build', (Join-Path $stageProject 'PetDaDogCSharp.csproj'), '--configuration', 'Debug', '--nologo') 'build-editor'
# Godot initializes the project theme before first importing its textures. Bootstrap
# only this fresh staging copy without it, then validate the actual saved configuration.
try {
    [IO.File]::WriteAllText($stagedConfigPath, ($stagedConfig -replace '(?m)^theme/custom=.*\r?\n?', ''))
    $null = Invoke-CheckedProcess $GodotPath @('--headless', '--path', $stageProject, '--editor', '--import') 'import-textures'
} finally {
    [IO.File]::WriteAllText($stagedConfigPath, $stagedConfig)
}
$null = Invoke-CheckedProcess $GodotPath @('--headless', '--path', $stageProject, '--editor', '--import') 'import-resources'
$null = Invoke-CheckedProcess $dotnet.Source @('build', (Join-Path $stageProject 'PetDaDogCSharp.csproj'), '--configuration', 'Release', '--nologo') 'build-release'
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
[IO.File]::WriteAllText((Join-Path $packagePath 'steam_appid.txt'), $steamAppId + [Environment]::NewLine)

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

@(
    'Pet Da Dog Windows build',
    "Built at (UTC): $([DateTime]::UtcNow.ToString('yyyy-MM-dd HH:mm:ss'))",
    "Godot: $version",
    "Worker: $($backendUri.AbsoluteUri)",
    "Steam AppID: $steamAppId",
    'Validation: Release build, resource import, complete export, Steam-disabled launch smoke passed.',
    'Keep this entire folder together. Launch PetDaDogCSharp.exe with Steam running.',
    'This package contains only the client; Steam publisher keys remain on the backend.'
) | Set-Content -LiteralPath (Join-Path $packagePath 'Build-info.txt') -Encoding utf8

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
