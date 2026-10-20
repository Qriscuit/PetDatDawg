param(
    [switch]$TransportSmoke,
    [switch]$SkipBuild,
    [string]$NodePath,
    [string]$GodotPath = 'W:\Godot_v4.6.3-stable_mono_win64\Godot_v4.6.3-stable_mono_win64_console.exe'
)

$ErrorActionPreference = 'Stop'
$testRepo = Split-Path -Parent $PSScriptRoot
$testProject = Join-Path $testRepo 'Pet_Da_Dog_CSharp'
$testWorkerRoot = Join-Path $testRepo 'PetDaDog.Worker'
if (-not $NodePath) {
    $testBundledNode = Join-Path $env:USERPROFILE '.cache\codex-runtimes\codex-primary-runtime\dependencies\node\bin\node.exe'
    $NodePath = if (Test-Path -LiteralPath $testBundledNode) { $testBundledNode } else { (Get-Command node -ErrorAction Stop).Source }
}
$testNodeVersion = & $NodePath --version
if ([version]$testNodeVersion.TrimStart('v') -lt [version]'22.13.0') { throw 'Node 22.13 or newer is required. Pass -NodePath to a supported node.exe.' }
if (-not (Test-Path -LiteralPath (Join-Path $testWorkerRoot 'node_modules\wrangler\bin\wrangler.js'))) { throw 'Run npm ci in PetDaDog.Worker first.' }
if (-not $TransportSmoke -and -not (Test-Path -LiteralPath $GodotPath)) { throw 'Pass -GodotPath for the Godot 4.6.3 Mono runtime.' }
if (Get-NetTCPConnection -LocalPort 8787 -State Listen -ErrorAction SilentlyContinue) { throw 'Port 8787 is occupied. Stop the previous local Worker before starting an isolated test economy.' }

$testRunRoot = Join-Path $testProject ('.godot\isolated-economy\' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $testRunRoot | Out-Null
$testEnvironmentNames = @('PDD_DEV_ECONOMY','PDD_BACKEND_URL','PDD_DISABLE_STEAM','PDD_DISABLE_GLOBAL_INPUT','PDD_DISABLE_TRAY','APPDATA','LOCALAPPDATA','TEMP','TMP','WRANGLER_SEND_METRICS','XDG_CONFIG_HOME','WRANGLER_LOG_PATH')
$testPreviousEnvironment = @{}
foreach ($testName in $testEnvironmentNames) { $testPreviousEnvironment[$testName] = [Environment]::GetEnvironmentVariable($testName) }
$testWorkerProcess = $null

function Stop-TestProcessTree([int]$ProcessId) {
    foreach ($testChild in @(Get-CimInstance Win32_Process -Filter "ParentProcessId = $ProcessId" -ErrorAction SilentlyContinue)) {
        Stop-TestProcessTree -ProcessId $testChild.ProcessId
    }
    Stop-Process -Id $ProcessId -Force -ErrorAction SilentlyContinue
}

try {
    $env:PDD_DEV_ECONOMY = '1'
    $env:PDD_BACKEND_URL = 'http://127.0.0.1:8787/'
    $env:PDD_DISABLE_STEAM = '1'
    $env:PDD_DISABLE_GLOBAL_INPUT = '1'
    $env:PDD_DISABLE_TRAY = '1'
    $env:APPDATA = Join-Path $testRunRoot 'Roaming'
    $env:LOCALAPPDATA = Join-Path $testRunRoot 'Local'
    $env:TEMP = Join-Path $testRunRoot 'Temp'
    $env:TMP = $env:TEMP
    $env:WRANGLER_SEND_METRICS = 'false'
    $env:XDG_CONFIG_HOME = Join-Path $testRunRoot 'wrangler-config'
    $env:WRANGLER_LOG_PATH = Join-Path $testRunRoot 'wrangler-logs'
    New-Item -ItemType Directory -Force -Path $env:APPDATA,$env:LOCALAPPDATA,$env:TEMP,$env:XDG_CONFIG_HOME,$env:WRANGLER_LOG_PATH | Out-Null
    $testPersist = Join-Path $testRunRoot 'worker-state'
    $testStdout = Join-Path $testRunRoot 'worker.stdout.log'
    $testStderr = Join-Path $testRunRoot 'worker.stderr.log'
    $testWorkerArgs = @('node_modules/wrangler/bin/wrangler.js','dev','--local','--config','wrangler.test.jsonc','--config','wrangler.fake-steam.jsonc','--persist-to',('"' + $testPersist + '"'),'--log-level','warn')
    $testWorkerProcess = Start-Process -FilePath $NodePath -ArgumentList $testWorkerArgs -WorkingDirectory $testWorkerRoot -WindowStyle Hidden -PassThru -RedirectStandardOutput $testStdout -RedirectStandardError $testStderr
    $testDeadline = [DateTime]::UtcNow.AddSeconds(40)
    $testReady = $false
    while ([DateTime]::UtcNow -lt $testDeadline) {
        if ($testWorkerProcess.HasExited) { throw "Local Worker exited before startup. Read $testStderr" }
        try { Invoke-WebRequest -Uri 'http://127.0.0.1:8787/health' -TimeoutSec 1 -ErrorAction Stop | Out-Null; $testReady = $true; break }
        catch { Start-Sleep -Milliseconds 200 }
    }
    if (-not $testReady) { throw "The local Worker did not start. Read $testStderr" }
    Write-Output "Isolated fake economy started. Test profile and logs: $testRunRoot"
    if ($TransportSmoke) {
        & dotnet run --project (Join-Path $testRepo 'Tests\BackendClientHarness\BackendClientHarness.csproj') -- --local-economy
        if ($LASTEXITCODE -ne 0) { throw "Local client/Worker integration failed ($LASTEXITCODE)." }
    }
    else {
        if (-not $SkipBuild) {
            & $GodotPath --headless --editor --path $testProject --import --quit --no-header
            if ($LASTEXITCODE -ne 0) { throw 'Godot import failed.' }
            & dotnet build (Join-Path $testProject 'PetDaDogCSharp.csproj') -c Debug
            if ($LASTEXITCODE -ne 0) { throw 'Debug client build failed.' }
        }
        Write-Output 'Open the Shop for developer seed, cooldown reset, and dig preview controls. Closing the game stops both local Workers.'
        & $GodotPath --path $testProject --scene 'res://Main.tscn' --no-header
        if ($LASTEXITCODE -ne 0) { throw "Debug client exited with status $LASTEXITCODE." }
    }
}
finally {
    if ($testWorkerProcess) { Stop-TestProcessTree -ProcessId $testWorkerProcess.Id }
    foreach ($testName in $testEnvironmentNames) { [Environment]::SetEnvironmentVariable($testName, $testPreviousEnvironment[$testName]) }
}
