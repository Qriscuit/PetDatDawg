param(
    [Parameter(Mandatory=$true)][ValidatePattern('^[0-9]{17}$')][string]$ValidatedTestSteamId,
    [string]$GodotPath = 'W:\Godot_v4.6.3-stable_mono_win64\Godot_v4.6.3-stable_mono_win64_console.exe',
    [string]$TemplatesDirectory,
    [switch]$PreserveLaunchGameId,
    [ValidatePattern('^[a-fA-F0-9]{64}$')][string]$ExpectedPricedSchemaSha256 = 'a4eb9ae150a141d3728c6ca2706dc546111becd95731cb5cb07d03b1b2b5916e'
)

$ErrorActionPreference = 'Stop'
$sandboxRepo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$sandboxSource = Join-Path $sandboxRepo 'Pet_Da_Dog_CSharp'
$sandboxRoot = Join-Path $sandboxSource '.godot\steam-checkout-sandbox'
$sandboxRun = Join-Path $sandboxRoot ([DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0,8))
$sandboxProject = Join-Path $sandboxRun 'project'
$sandboxPackage = Join-Path $sandboxRun 'package'
if (-not $TemplatesDirectory) { $TemplatesDirectory = Join-Path $sandboxSource '.godot\build-tools\templates-4.6.3' }
$sandboxDebugTemplate = Join-Path $TemplatesDirectory 'windows_debug_x86_64.exe'
if (-not (Test-Path -LiteralPath $GodotPath) -or -not (Test-Path -LiteralPath $sandboxDebugTemplate)) { throw 'Provide Godot 4.6.3 Mono and its Windows debug export template.' }
if ((& $GodotPath --version) -notmatch '^4\.6\.3\.stable\.mono\.') { throw 'Only Godot 4.6.3 Mono is supported.' }

function Copy-SandboxSource([string]$Source, [string]$Destination) {
    New-Item -ItemType Directory -Force -Path $Destination | Out-Null
    foreach ($sandboxItem in Get-ChildItem -LiteralPath $Source -Force) {
        if ($sandboxItem.PSIsContainer) {
            if ($sandboxItem.Name -notin @('.godot','.git','.vs','bin','obj','export_templates','node_modules')) { Copy-SandboxSource $sandboxItem.FullName (Join-Path $Destination $sandboxItem.Name) }
        }
        elseif ($sandboxItem.Name -notmatch '^(?:\.env(?:\..*)?|\.dev\.vars(?:\..*)?|export_credentials\.cfg)$' -and $sandboxItem.Extension -notin @('.pem','.key','.pfx','.p12')) {
            Copy-Item -LiteralPath $sandboxItem.FullName -Destination $Destination
        }
    }
}
function Run-SandboxTool([string]$File, [string[]]$Arguments, [string]$Label) {
    $sandboxOut = Join-Path $sandboxRun ($Label + '.stdout.log')
    $sandboxErr = Join-Path $sandboxRun ($Label + '.stderr.log')
    $sandboxQuoted = $Arguments | ForEach-Object { '"' + $_.Replace('"','\"') + '"' }
    $sandboxProcess = Start-Process -FilePath $File -ArgumentList $sandboxQuoted -WorkingDirectory $sandboxProject -WindowStyle Hidden -PassThru -RedirectStandardOutput $sandboxOut -RedirectStandardError $sandboxErr
    $null = $sandboxProcess.Handle
    if (-not $sandboxProcess.WaitForExit(300000)) { $sandboxProcess.Kill(); throw "$Label timed out. Logs: $sandboxRun" }
    $sandboxProcess.WaitForExit()
    $sandboxLog = (Get-Content -LiteralPath $sandboxOut -Raw) + (Get-Content -LiteralPath $sandboxErr -Raw)
    if ($sandboxProcess.ExitCode -ne 0 -or $sandboxLog -match '(?m)^\s*(?:SCRIPT ERROR:|ERROR: (?!Failed to read the root certificate store\.)|Build FAILED)') { throw "$Label failed. Logs: $sandboxRun`n$sandboxLog" }
    Write-Host "$Label passed."
    return $sandboxLog
}

New-Item -ItemType Directory -Force -Path $sandboxProject,$sandboxPackage | Out-Null
Copy-SandboxSource $sandboxSource $sandboxProject
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'CheckoutSandboxEnvironment.cs'),(Join-Path $PSScriptRoot 'CheckoutSandbox.tscn') -Destination $sandboxProject
$sandboxCode = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'CheckoutSandbox.cs')).Replace('__EXPECTED_SCHEMA_SHA256__',$ExpectedPricedSchemaSha256.ToLowerInvariant()).Replace('__VALIDATED_TEST_STEAM_ID__',$ValidatedTestSteamId).Replace('__PRESERVE_LAUNCH_GAME_ID__',$PreserveLaunchGameId.IsPresent.ToString().ToLowerInvariant())
[IO.File]::WriteAllText((Join-Path $sandboxProject 'CheckoutSandbox.cs'),$sandboxCode)
# Instrument and optionally alter only this generated copy, never the shipping source.
$sandboxSteamAppIdPath = Join-Path $sandboxProject 'Scripts\SteamAppId.cs'
$sandboxSteamAppId = [IO.File]::ReadAllText($sandboxSteamAppIdPath).Replace("`r`n","`n")
$sandboxOriginalConfigure = @'
	public static void ConfigureProcess(uint appId)
	{
		var value = appId.ToString(CultureInfo.InvariantCulture);
		Environment.SetEnvironmentVariable("SteamAppId", value, EnvironmentVariableTarget.Process);
		Environment.SetEnvironmentVariable("SteamGameId", value, EnvironmentVariableTarget.Process);
	}
'@
$sandboxOriginalConfigure = $sandboxOriginalConfigure.Replace("`r`n","`n")
if ([regex]::Matches($sandboxSteamAppId,[regex]::Escape($sandboxOriginalConfigure)).Count -ne 1) {
    throw 'SteamAppId.ConfigureProcess changed: refusing to patch the generated sandbox without reviewing its expected source.'
}
$sandboxReplacementConfigure = $sandboxOriginalConfigure.Replace("`t`tvar value =", "`t`tCheckoutSandbox.LogSteamLaunchEnvironment(`"before-configure`");`n`t`tvar value =")
if ($PreserveLaunchGameId) {
    $sandboxGameIdAssignment = 'Environment.SetEnvironmentVariable("SteamGameId", value, EnvironmentVariableTarget.Process);'
    $sandboxReplacementConfigure = $sandboxReplacementConfigure.Replace($sandboxGameIdAssignment, "if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(`"SteamGameId`")))`n`t`t`t$sandboxGameIdAssignment")
}
$sandboxReplacementConfigure = $sandboxReplacementConfigure.Substring(0,$sandboxReplacementConfigure.Length - 2) + "`t`tCheckoutSandbox.LogSteamLaunchEnvironment(`"after-configure-before-init`");`n`t}"
[IO.File]::WriteAllText($sandboxSteamAppIdPath,$sandboxSteamAppId.Replace($sandboxOriginalConfigure,$sandboxReplacementConfigure))
$sandboxCsproj = Join-Path $sandboxProject 'PetDaDogCSharp.csproj'
$sandboxXml = [IO.File]::ReadAllText($sandboxCsproj).Replace('<Nullable>enable</Nullable>', '<Nullable>enable</Nullable><DefineConstants>$(DefineConstants);CHECKOUT_SANDBOX</DefineConstants>')
[IO.File]::WriteAllText($sandboxCsproj,$sandboxXml)
$sandboxConfigPath = Join-Path $sandboxProject 'project.godot'
$sandboxConfig = [IO.File]::ReadAllText($sandboxConfigPath)
$sandboxConfig = $sandboxConfig.Replace('run/main_scene="res://Main.tscn"','run/main_scene="res://CheckoutSandbox.tscn"')
$sandboxConfig = $sandboxConfig.Replace('config/name="PetDaDogCSharp"', "config/name=`"PetDaDogCheckoutSandbox`"`nconfig/use_custom_user_dir=true`nconfig/custom_user_dir_name=`"PetDaDogCheckoutSandbox`"")
$sandboxConfig = $sandboxConfig.Replace('[autoload]', "[autoload]`n`nCheckoutSandboxEnvironment=`"*res://CheckoutSandboxEnvironment.cs`"")
[IO.File]::WriteAllText($sandboxConfigPath,$sandboxConfig)
$sandboxPresetPath = Join-Path $sandboxProject 'export_presets.cfg'
$sandboxPreset = [IO.File]::ReadAllText($sandboxPresetPath) -replace '(?m)^custom_template/debug=.*$',('custom_template/debug="' + $sandboxDebugTemplate.Replace('\','/') + '"')
[IO.File]::WriteAllText($sandboxPresetPath,$sandboxPreset)
$sandboxDotnet = (Get-Command dotnet).Source
$null = Run-SandboxTool $sandboxDotnet @('build',$sandboxCsproj,'-c','Debug','--nologo') 'build-debug'
try {
    [IO.File]::WriteAllText($sandboxConfigPath,($sandboxConfig -replace '(?m)^theme/custom=.*\r?\n?',''))
    $null = Run-SandboxTool $GodotPath @('--headless','--path',$sandboxProject,'--editor','--import') 'import-textures'
}
finally { [IO.File]::WriteAllText($sandboxConfigPath,$sandboxConfig) }
$null = Run-SandboxTool $GodotPath @('--headless','--path',$sandboxProject,'--editor','--import') 'import-resources'
$sandboxExe = Join-Path $sandboxPackage 'PetDaDogCheckoutSandbox.exe'
$null = Run-SandboxTool $GodotPath @('--headless','--path',$sandboxProject,'--export-debug','Windows Desktop',$sandboxExe) 'export-debug'
[IO.File]::WriteAllText((Join-Path $sandboxPackage 'steam_appid.txt'),"4817200`n")
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'README.md') -Destination (Join-Path $sandboxPackage 'README.txt')
$sandboxPreviousOffline = $env:PDD_CHECKOUT_SANDBOX_OFFLINE
try {
    $env:PDD_CHECKOUT_SANDBOX_OFFLINE = '1'
    $sandboxSmoke = Run-SandboxTool $sandboxExe @('--') 'offline-launch'
    if ($sandboxSmoke -notmatch 'CHECKOUT_SANDBOX_OFFLINE_PASS:') { throw 'The offline export smoke marker was missing.' }
}
finally { $env:PDD_CHECKOUT_SANDBOX_OFFLINE = $sandboxPreviousOffline }
[IO.File]::WriteAllText((Join-Path $sandboxRoot 'latest-package.txt'),$sandboxPackage)
Write-Output "CHECKOUT_SANDBOX_BUILD_READY: $sandboxExe"
Write-Output "Expected priced schema SHA256: $($ExpectedPricedSchemaSha256.ToLowerInvariant())"
Write-Output "Preserve inherited launch GameID diagnostic: $($PreserveLaunchGameId.IsPresent)"
Write-Output 'Checkout remains locked. This builder never creates checkout-approval.json or starts a purchase.'
