param(
    [switch]$AllowLiveSteam,
    [ValidateRange(0, 3)][int]$GrantCount = 0,
    [ValidateRange(30, 600)][int]$TimeoutSeconds = 150,
    [string]$BackendUrl = '',
    [string]$GodotPath = 'W:\Godot_v4.6.3-stable_mono_win64\Godot_v4.6.3-stable_mono_win64_console.exe',
    [switch]$SkipBuild,
    [switch]$UseDogInput,
    [switch]$WorkerOnly,
    [string]$RetryEventId = '',
    [long]$RetryExpectedPets = 0,
    [string]$LogDirectory = '',
    [ValidateSet('any', 'enabled', 'disabled')][string]$ExpectedShopState = 'any'
)

$ErrorActionPreference = 'Stop'
if (-not $AllowLiveSteam) { throw 'Pass -AllowLiveSteam to explicitly permit real Steam authentication and Worker inventory requests. GrantCount defaults to 0.' }
if ($RetryEventId -or $PSBoundParameters.ContainsKey('RetryExpectedPets')) {
    throw 'Legacy RetryEventId is disabled in the batched client: an input GUID is not a batch operation ID. Replaying it here could award new Pets. Use the preserved v1 runner only for an original v1 operation; batch recovery must retain its full frozen payload.'
}
if (-not (Test-Path -LiteralPath $GodotPath -PathType Leaf)) { throw "Godot 4.6.3 was not found at $GodotPath." }
$liveSmokeProjectPath = Split-Path -Parent $PSScriptRoot
$liveSmokeNames = @('PDD_LIVE_STEAM_SMOKE', 'PDD_LIVE_SMOKE_GRANTS', 'PDD_LIVE_SMOKE_TIMEOUT_SECONDS', 'PDD_LIVE_SMOKE_DOG_INPUT', 'PDD_LIVE_SMOKE_WORKER_ONLY', 'PDD_LIVE_SMOKE_RETRY_EVENT_ID', 'PDD_LIVE_SMOKE_RETRY_EXPECTED_PETS', 'PDD_LIVE_SMOKE_EXPECT_SHOP_ENABLED', 'PDD_STEAM_APP_ID', 'PDD_BACKEND_URL', 'PDD_DISABLE_STEAM', 'PDD_DISABLE_GLOBAL_INPUT', 'PDD_DISABLE_TRAY', 'APPDATA', 'LOCALAPPDATA', 'TEMP', 'TMP')
$liveSmokePrevious = @{}
foreach ($liveSmokeName in $liveSmokeNames) { $liveSmokePrevious[$liveSmokeName] = [Environment]::GetEnvironmentVariable($liveSmokeName, 'Process') }
$liveSmokeProcess = $null
try {
    $env:PDD_LIVE_STEAM_SMOKE = '1'
    $env:PDD_LIVE_SMOKE_GRANTS = $GrantCount.ToString([Globalization.CultureInfo]::InvariantCulture)
    $env:PDD_LIVE_SMOKE_TIMEOUT_SECONDS = $TimeoutSeconds.ToString([Globalization.CultureInfo]::InvariantCulture)
    $env:PDD_LIVE_SMOKE_DOG_INPUT = if ($UseDogInput) { '1' } else { '0' }
    $env:PDD_LIVE_SMOKE_WORKER_ONLY = if ($WorkerOnly) { '1' } else { '0' }
    $env:PDD_LIVE_SMOKE_RETRY_EVENT_ID = ''
    $env:PDD_LIVE_SMOKE_RETRY_EXPECTED_PETS = ''
    $env:PDD_LIVE_SMOKE_EXPECT_SHOP_ENABLED = if ($ExpectedShopState -eq 'enabled') { '1' } elseif ($ExpectedShopState -eq 'disabled') { '0' } else { '' }
    $env:PDD_STEAM_APP_ID = '4817200'
    $env:PDD_DISABLE_STEAM = '0'
    $env:PDD_DISABLE_GLOBAL_INPUT = '1'
    $env:PDD_DISABLE_TRAY = '1'
    if ($BackendUrl) { $env:PDD_BACKEND_URL = $BackendUrl }
    $env:APPDATA = Join-Path $liveSmokeProjectPath '.godot\live-steam-smoke-home\Roaming'
    $env:LOCALAPPDATA = Join-Path $liveSmokeProjectPath '.godot\live-steam-smoke-home\Local'
    $env:TEMP = Join-Path $liveSmokeProjectPath '.godot\live-steam-smoke-home\Temp'
    $env:TMP = $env:TEMP
    New-Item -ItemType Directory -Force -Path $env:APPDATA, $env:LOCALAPPDATA, $env:TEMP | Out-Null
    if (-not $LogDirectory) { $LogDirectory = Join-Path $liveSmokeProjectPath ('.godot\live-steam-smoke-' + (Get-Date -Format 'yyyyMMdd-HHmmss')) }
    New-Item -ItemType Directory -Force -Path $LogDirectory | Out-Null
    $LogDirectory = (Resolve-Path -LiteralPath $LogDirectory).Path
    if (-not $SkipBuild) {
        & $GodotPath --headless --editor --path $liveSmokeProjectPath --import --quit --no-header
        if ($LASTEXITCODE -ne 0) { throw "Godot import failed ($LASTEXITCODE)." }
        dotnet build (Join-Path $liveSmokeProjectPath 'PetDaDogCSharp.csproj')
        if ($LASTEXITCODE -ne 0) { throw "Client build failed ($LASTEXITCODE)." }
    }
    Write-Output "Running real Steam/Worker checks for AppID 4817200; requested grants: $GrantCount. Keep other clients closed while verifying exact increments."
    if ($WorkerOnly) { Write-Output 'WorkerOnly mode verifies the Steam inventory responses supplied by the trusted Worker; independent native inventory verification is skipped.' }
    $liveSmokeStdout = Join-Path $LogDirectory 'stdout.log'
    $liveSmokeStderr = Join-Path $LogDirectory 'stderr.log'
    $liveSmokeArguments = @('--headless', '--path', ('"' + $liveSmokeProjectPath + '"'), '--scene', 'res://Tests/LiveSteamSmokeTest.tscn', '--no-header')
    $liveSmokeProcess = Start-Process -FilePath $GodotPath -ArgumentList $liveSmokeArguments -WorkingDirectory $liveSmokeProjectPath -WindowStyle Hidden -PassThru -RedirectStandardOutput $liveSmokeStdout -RedirectStandardError $liveSmokeStderr
    $null = $liveSmokeProcess.Handle
    if (-not $liveSmokeProcess.WaitForExit(($TimeoutSeconds + 20) * 1000)) {
        Stop-Process -Id $liveSmokeProcess.Id -Force
        throw 'The live test exceeded its outer timeout. A sent grant may already have reached Steam; inspect inventory before requesting another grant test.'
    }
    $liveSmokeProcess.WaitForExit()
    $liveSmokeOutput = @(Get-Content -LiteralPath $liveSmokeStdout; Get-Content -LiteralPath $liveSmokeStderr)
    $liveSmokeOutput | Write-Output
    Write-Output "Live test logs: $LogDirectory"
    $liveSmokeExpectedMarker = if ($WorkerOnly) { '^LIVE_WORKER_SMOKE_PASS:' } else { '^LIVE_STEAM_SMOKE_PASS:' }
    if ($liveSmokeProcess.ExitCode -ne 0 -or -not ($liveSmokeOutput -match $liveSmokeExpectedMarker)) {
        throw "Live Steam smoke did not pass (exit $($liveSmokeProcess.ExitCode)). Review the logs at $LogDirectory."
    }
}
finally {
    if ($liveSmokeProcess -and -not $liveSmokeProcess.HasExited) { Stop-Process -Id $liveSmokeProcess.Id -Force }
    foreach ($liveSmokeName in $liveSmokeNames) { [Environment]::SetEnvironmentVariable($liveSmokeName, $liveSmokePrevious[$liveSmokeName], 'Process') }
}
