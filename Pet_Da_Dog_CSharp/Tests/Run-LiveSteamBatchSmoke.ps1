param(
    [switch]$AllowLiveSteam,
    [Parameter(Mandatory = $true)][string]$BackendUrl,
    [Parameter(Mandatory = $true)][string]$PlanDirectory,
    [switch]$Resume,
    [switch]$PrepareOnly,
    [switch]$AllowLoopbackProxy,
    [switch]$TransportProbe,
    [switch]$VerifyDeployment,
    [switch]$RequireNativeBaseline,
    [ValidateRange(60, 900)][int]$TimeoutSeconds = 300,
    [string]$GodotPath = 'W:\Godot_v4.6.3-stable_mono_win64\Godot_v4.6.3-stable_mono_win64_console.exe',
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
if (-not $PrepareOnly -and -not $AllowLiveSteam) { throw 'Pass -AllowLiveSteam only with authorization for this fixed 302-Pet test. Resume an existing plan after any uncertain result.' }
$batchProjectPath = Split-Path -Parent $PSScriptRoot
$batchIgnoredRoot = [IO.Path]::GetFullPath((Join-Path $batchProjectPath '.godot')) + [IO.Path]::DirectorySeparatorChar
$batchPlanRoot = [IO.Path]::GetFullPath($PlanDirectory)
if (-not $batchPlanRoot.StartsWith($batchIgnoredRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'PlanDirectory must be inside the client project .godot directory.' }
$batchPlanFile = Join-Path $batchPlanRoot 'batch-plan.json'
New-Item -ItemType Directory -Force -Path $batchPlanRoot | Out-Null
$batchPreparedFile = Join-Path $batchPlanRoot 'prepared-batches.json'
if ($PrepareOnly) {
    if (Test-Path -LiteralPath $batchPlanFile -PathType Leaf) { throw 'This plan already reached live preparation. Preserve it and use -Resume.' }
    if (-not (Test-Path -LiteralPath $batchPreparedFile -PathType Leaf)) {
        $batchRunId = [Guid]::NewGuid().ToString('D')
        $batchPreparedAttempts = foreach ($batchSpec in @(@{firstSequence=1;count=2},@{firstSequence=3;count=300})) {
            @{request=@{clientEventId=[Guid]::NewGuid().ToString('D');runId=$batchRunId;firstSequence=$batchSpec.firstSequence;count=$batchSpec.count};overlapProbe=@{clientEventId=[Guid]::NewGuid().ToString('D');runId=$batchRunId;firstSequence=$batchSpec.firstSequence;count=1}}
        }
        $batchPreparedJson = @{version=1;attempts=@($batchPreparedAttempts)} | ConvertTo-Json -Depth 8
        [IO.File]::WriteAllText($batchPreparedFile,$batchPreparedJson)
    }
    Write-Output "Prepared immutable batch payloads (no Steam/network activity): $batchPreparedFile"
    return
}
if ($Resume.IsPresent -ne (Test-Path -LiteralPath $batchPlanFile -PathType Leaf)) { throw 'Use -Resume for an existing saved plan. A new plan directory requires authorization for a separate test.' }
if (-not $Resume -and -not (Test-Path -LiteralPath $batchPreparedFile -PathType Leaf)) { throw 'Use -PrepareOnly before live verification so the approved payloads exist in advance.' }
if ($VerifyDeployment -and -not $Resume) { throw 'Deployment verification requires -Resume of the completed preflight plan.' }
if (-not (Test-Path -LiteralPath $GodotPath -PathType Leaf)) { throw 'Godot 4.6.3 is required.' }
$batchNames = @('PDD_LIVE_BATCH_SMOKE','PDD_LIVE_BATCH_URL','PDD_LIVE_BATCH_PLAN_DIRECTORY','PDD_LIVE_BATCH_RESUME','PDD_LIVE_BATCH_ALLOW_LOOPBACK','PDD_LIVE_BATCH_TRANSPORT_PROBE','PDD_LIVE_BATCH_VERIFY_DEPLOYMENT','PDD_LIVE_BATCH_REQUIRE_NATIVE','PDD_LIVE_BATCH_TIMEOUT_SECONDS','PDD_STEAM_APP_ID','PDD_DISABLE_STEAM','PDD_DISABLE_GLOBAL_INPUT','PDD_DISABLE_TRAY','APPDATA','LOCALAPPDATA','TEMP','TMP')
$batchPrevious = @{}
foreach ($batchName in $batchNames) { $batchPrevious[$batchName] = [Environment]::GetEnvironmentVariable($batchName, 'Process') }
$batchProcess = $null
try {
    $env:PDD_LIVE_BATCH_SMOKE = '1'
    $env:PDD_LIVE_BATCH_URL = $BackendUrl
    $env:PDD_LIVE_BATCH_PLAN_DIRECTORY = $batchPlanRoot
    $env:PDD_LIVE_BATCH_RESUME = if ($Resume) { '1' } else { '0' }
    $env:PDD_LIVE_BATCH_ALLOW_LOOPBACK = if ($AllowLoopbackProxy) { '1' } else { '0' }
    $env:PDD_LIVE_BATCH_TRANSPORT_PROBE = if ($TransportProbe) { '1' } else { '0' }
    $env:PDD_LIVE_BATCH_VERIFY_DEPLOYMENT = if ($VerifyDeployment) { '1' } else { '0' }
    $env:PDD_LIVE_BATCH_REQUIRE_NATIVE = if ($RequireNativeBaseline) { '1' } else { '0' }
    $env:PDD_LIVE_BATCH_TIMEOUT_SECONDS = $TimeoutSeconds.ToString([Globalization.CultureInfo]::InvariantCulture)
    $env:PDD_STEAM_APP_ID = '4817200'
    $env:PDD_DISABLE_STEAM = '0'
    $env:PDD_DISABLE_GLOBAL_INPUT = '1'
    $env:PDD_DISABLE_TRAY = '1'
    $env:APPDATA = Join-Path $batchPlanRoot 'profile\Roaming'
    $env:LOCALAPPDATA = Join-Path $batchPlanRoot 'profile\Local'
    $env:TEMP = Join-Path $batchPlanRoot 'profile\Temp'
    $env:TMP = $env:TEMP
    New-Item -ItemType Directory -Force -Path $env:APPDATA,$env:LOCALAPPDATA,$env:TEMP | Out-Null
    if (-not $SkipBuild) {
        & $GodotPath --headless --editor --path $batchProjectPath --import --quit --no-header
        if ($LASTEXITCODE -ne 0) { throw 'Godot import failed before live verification.' }
        dotnet build (Join-Path $batchProjectPath 'PetDaDogCSharp.csproj')
        if ($LASTEXITCODE -ne 0) { throw 'Build failed before live verification.' }
    }
    $batchRunLogRoot = Join-Path $batchPlanRoot ('run-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
    New-Item -ItemType Directory -Path $batchRunLogRoot | Out-Null
    $batchStdout = Join-Path $batchRunLogRoot 'stdout.log'
    $batchStderr = Join-Path $batchRunLogRoot 'stderr.log'
    if ($VerifyDeployment) {
        Write-Output 'Verifying deployment by replaying the same saved batch IDs (intended new Pets: 0). No dog, global input, Shop, purchases, recycling or acknowledgments run. Close other clients before starting.'
    } else {
        Write-Output 'Running the fixed two-batch Steam test (intended new Pets: 2 + 300). No dog, global input, Shop, purchases, recycling or acknowledgments run. Close other clients before starting.'
    }
    $batchArguments = @('--headless','--path',('"' + $batchProjectPath + '"'),'--scene','res://Tests/LiveSteamBatchSmokeTest.tscn','--no-header')
    $batchProcess = Start-Process -FilePath $GodotPath -ArgumentList $batchArguments -WorkingDirectory $batchProjectPath -WindowStyle Hidden -PassThru -RedirectStandardOutput $batchStdout -RedirectStandardError $batchStderr
    $null = $batchProcess.Handle
    if (-not $batchProcess.WaitForExit(($TimeoutSeconds + 30) * 1000)) {
        Stop-Process -Id $batchProcess.Id -Force
        throw 'The bounded test timed out. Preserve this plan and resume it; never create replacement batch IDs.'
    }
    $batchProcess.WaitForExit()
    # Steam's own diagnostic stream may print its account ID. Keep our durable
    # test logs at the same hashed-identity policy as the verification scene.
    foreach ($batchLogFile in @($batchStdout,$batchStderr)) {
        $batchSanitized = [IO.File]::ReadAllText($batchLogFile) -replace '(Steam ID:\s*)\d+', '$1[redacted]'
        [IO.File]::WriteAllText($batchLogFile, $batchSanitized)
    }
    $batchOutput = @(Get-Content -LiteralPath $batchStdout; Get-Content -LiteralPath $batchStderr)
    $batchOutput | Write-Output
    Write-Output "Saved immutable plan: $batchPlanFile"
    Write-Output "Test logs: $batchRunLogRoot"
    $batchPassMarker = if ($VerifyDeployment) { '^LIVE_BATCH_DEPLOYMENT_PASS:' } else { '^LIVE_BATCH_SMOKE_PASS:' }
    if ($batchProcess.ExitCode -ne 0 -or -not ($batchOutput -match $batchPassMarker)) { throw 'Live batch verification did not pass. Preserve the saved plan and use -Resume with this same endpoint.' }
}
finally {
    if ($batchProcess -and -not $batchProcess.HasExited) { Stop-Process -Id $batchProcess.Id -Force }
    foreach ($batchName in $batchNames) { [Environment]::SetEnvironmentVariable($batchName, $batchPrevious[$batchName], 'Process') }
}
