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
    [string]$LogDirectory = ''
)

$ErrorActionPreference = 'Stop'
if (-not $AllowLiveSteam) { throw 'Pass -AllowLiveSteam to explicitly permit real Steam authentication and Worker inventory requests. GrantCount defaults to 0.' }
$liveSmokeRetryGuid = [Guid]::Empty
if ($RetryEventId) {
    if (-not $WorkerOnly -or $GrantCount -ne 1 -or $UseDogInput) { throw 'RetryEventId requires -WorkerOnly -GrantCount 1 and no -UseDogInput; it retries the existing request without creating another dog click.' }
    if (-not [Guid]::TryParse($RetryEventId, [ref]$liveSmokeRetryGuid) -or $liveSmokeRetryGuid -eq [Guid]::Empty) { throw 'RetryEventId must be the non-empty GUID recorded by LIVE_PET_REQUESTED.' }
    if (-not $PSBoundParameters.ContainsKey('RetryExpectedPets') -or $RetryExpectedPets -le 0) { throw 'Supply -RetryExpectedPets with the positive expected total recorded for the original request.' }
} elseif ($PSBoundParameters.ContainsKey('RetryExpectedPets')) { throw 'RetryExpectedPets requires RetryEventId.' }
if (-not (Test-Path -LiteralPath $GodotPath -PathType Leaf)) { throw "Godot 4.6.3 was not found at $GodotPath." }
$liveSmokeProjectPath = Split-Path -Parent $PSScriptRoot
$liveSmokeNames = @('PDD_LIVE_STEAM_SMOKE', 'PDD_LIVE_SMOKE_GRANTS', 'PDD_LIVE_SMOKE_TIMEOUT_SECONDS', 'PDD_LIVE_SMOKE_DOG_INPUT', 'PDD_LIVE_SMOKE_WORKER_ONLY', 'PDD_LIVE_SMOKE_RETRY_EVENT_ID', 'PDD_LIVE_SMOKE_RETRY_EXPECTED_PETS', 'PDD_STEAM_APP_ID', 'PDD_BACKEND_URL', 'PDD_DISABLE_STEAM', 'PDD_DISABLE_TRAY', 'APPDATA', 'LOCALAPPDATA', 'TEMP', 'TMP')
$liveSmokePrevious = @{}
foreach ($liveSmokeName in $liveSmokeNames) { $liveSmokePrevious[$liveSmokeName] = [Environment]::GetEnvironmentVariable($liveSmokeName, 'Process') }
$liveSmokeProcess = $null
try {
    $env:PDD_LIVE_STEAM_SMOKE = '1'
    $env:PDD_LIVE_SMOKE_GRANTS = $GrantCount.ToString([Globalization.CultureInfo]::InvariantCulture)
    $env:PDD_LIVE_SMOKE_TIMEOUT_SECONDS = $TimeoutSeconds.ToString([Globalization.CultureInfo]::InvariantCulture)
    $env:PDD_LIVE_SMOKE_DOG_INPUT = if ($UseDogInput) { '1' } else { '0' }
    $env:PDD_LIVE_SMOKE_WORKER_ONLY = if ($WorkerOnly) { '1' } else { '0' }
    $env:PDD_LIVE_SMOKE_RETRY_EVENT_ID = if ($RetryEventId) { $liveSmokeRetryGuid.ToString('D') } else { '' }
    $env:PDD_LIVE_SMOKE_RETRY_EXPECTED_PETS = if ($RetryEventId) { $RetryExpectedPets.ToString([Globalization.CultureInfo]::InvariantCulture) } else { '' }
    $env:PDD_STEAM_APP_ID = '4817200'
    $env:PDD_DISABLE_STEAM = '0'
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
    if ($RetryEventId) { Write-Output "Retrying the existing request $liveSmokeRetryGuid; original expected total: $RetryExpectedPets. This run does not generate another click GUID." }
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
    $liveSmokeExpectedMarker = if ($RetryEventId) { '^LIVE_WORKER_RETRY_PASS:' } elseif ($WorkerOnly) { '^LIVE_WORKER_SMOKE_PASS:' } else { '^LIVE_STEAM_SMOKE_PASS:' }
    if ($liveSmokeProcess.ExitCode -ne 0 -or -not ($liveSmokeOutput -match $liveSmokeExpectedMarker)) {
        throw "Live Steam smoke did not pass (exit $($liveSmokeProcess.ExitCode)). Review the logs at $LogDirectory."
    }
}
finally {
    if ($liveSmokeProcess -and -not $liveSmokeProcess.HasExited) { Stop-Process -Id $liveSmokeProcess.Id -Force }
    foreach ($liveSmokeName in $liveSmokeNames) { [Environment]::SetEnvironmentVariable($liveSmokeName, $liveSmokePrevious[$liveSmokeName], 'Process') }
}
