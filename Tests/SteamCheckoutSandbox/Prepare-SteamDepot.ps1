param(
    [string]$PackagePath
)

$ErrorActionPreference = 'Stop'
$depotRepo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$depotSandboxRoot = Join-Path $depotRepo 'Pet_Da_Dog_CSharp\.godot\steam-checkout-sandbox'
if (-not $PackagePath) { $PackagePath = (Get-Content -LiteralPath (Join-Path $depotSandboxRoot 'latest-package.txt') -Raw).Trim() }
$depotSource = [IO.Path]::GetFullPath($PackagePath).TrimEnd('\','/')
$depotRuntimeName = 'data_PetDaDogCSharp_windows_x86_64'
$depotRuntimeSource = Join-Path $depotSource $depotRuntimeName
$depotOutput = Join-Path $depotSandboxRoot ('steam-depot-upload\' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0,8))
$depotContent = Join-Path $depotOutput 'content'
$depotZip = Join-Path $depotOutput 'PetDaDog-4817200-depot-4817201-checkout-sandbox.zip'

foreach ($depotRequired in @('PetDaDogCheckoutSandbox.exe','PetDaDogCheckoutSandbox.pck',"$depotRuntimeName\PetDaDogCSharp.dll","$depotRuntimeName\PetDaDogCSharp.deps.json","$depotRuntimeName\PetDaDogCSharp.runtimeconfig.json","$depotRuntimeName\GodotSharp.dll","$depotRuntimeName\Steamworks.NET.dll","$depotRuntimeName\steam_api64.dll","$depotRuntimeName\coreclr.dll")) {
    if (-not (Test-Path -LiteralPath (Join-Path $depotSource $depotRequired) -PathType Leaf)) { throw "Missing sandbox export file: $depotRequired" }
}
if ((Get-Item -LiteralPath $depotSource).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Source package must not be a reparse point.' }

function Test-DepotExcluded([IO.FileSystemInfo]$Item) {
    return $Item.Name -match '(?i)^(?:steam_appid\.txt|checkout-approval\.json|checkout-session\.json|README(?:\..*)?|\.env(?:\..*)?|\.dev\.vars(?:\..*)?|export_credentials\.cfg)$' -or
        $Item.Name -match '(?i)(?:credential|secret|token|\.console\.exe$)' -or
        $Item.Extension -in @('.pdb','.pem','.key','.pfx','.p12','.log','.vdf')
}

$depotFiles = [Collections.Generic.List[object]]::new()
function Add-DepotFile([string]$Source, [string]$RelativePath) {
    $depotItem = Get-Item -LiteralPath $Source
    if ($depotItem.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Reparse point rejected: $RelativePath" }
    $depotFiles.Add([pscustomobject]@{ Source=$depotItem.FullName; Path=$RelativePath.Replace('\','/'); Length=$depotItem.Length; Sha256=(Get-FileHash -LiteralPath $depotItem.FullName -Algorithm SHA256).Hash.ToLowerInvariant() })
}
function Add-DepotRuntime([string]$Source, [string]$RelativePath) {
    $depotDirectory = Get-Item -LiteralPath $Source
    if ($depotDirectory.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Reparse point rejected: $RelativePath" }
    foreach ($depotItem in Get-ChildItem -LiteralPath $Source -Force) {
        if (Test-DepotExcluded $depotItem) { continue }
        $depotRelative = "$RelativePath/$($depotItem.Name)"
        if ($depotItem.PSIsContainer) { Add-DepotRuntime $depotItem.FullName $depotRelative }
        else { Add-DepotFile $depotItem.FullName $depotRelative }
    }
}
Add-DepotFile (Join-Path $depotSource 'PetDaDogCheckoutSandbox.exe') 'PetDaDogCSharp.exe'
Add-DepotFile (Join-Path $depotSource 'PetDaDogCheckoutSandbox.pck') 'PetDaDogCSharp.pck'
Add-DepotRuntime $depotRuntimeSource $depotRuntimeName

New-Item -ItemType Directory -Path $depotContent -Force | Out-Null
foreach ($depotFile in $depotFiles) {
    $depotDestination = Join-Path $depotContent $depotFile.Path
    New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($depotDestination)) -Force | Out-Null
    Copy-Item -LiteralPath $depotFile.Source -Destination $depotDestination
    if ((Get-FileHash -LiteralPath $depotDestination -Algorithm SHA256).Hash -ne $depotFile.Sha256) { throw "Staging hash mismatch: $($depotFile.Path)" }
}

# The exported scene exits after two seconds in offline mode. Separate Windows
# profile directories prevent it from reading or writing the active test session.
$depotSmokeRoot = Join-Path $depotOutput 'offline-smoke'
$depotSmokeEnvironment = @{
    APPDATA=(Join-Path $depotSmokeRoot 'Roaming')
    LOCALAPPDATA=(Join-Path $depotSmokeRoot 'Local')
    TEMP=(Join-Path $depotSmokeRoot 'Temp')
    TMP=(Join-Path $depotSmokeRoot 'Temp')
    PDD_CHECKOUT_SANDBOX_OFFLINE='1'
    PDD_DISABLE_STEAM='1'
    PDD_DISABLE_GLOBAL_INPUT='1'
    PDD_DISABLE_TRAY='1'
    PDD_DEV_ECONOMY='0'
}
foreach ($depotDirectory in @($depotSmokeEnvironment.APPDATA,$depotSmokeEnvironment.LOCALAPPDATA,$depotSmokeEnvironment.TEMP)) { New-Item -ItemType Directory -Path $depotDirectory -Force | Out-Null }
$depotStartInfo = [Diagnostics.ProcessStartInfo]::new()
$depotStartInfo.FileName = Join-Path $depotContent 'PetDaDogCSharp.exe'
$depotStartInfo.WorkingDirectory = $depotContent
$depotStartInfo.Arguments = '--'
$depotStartInfo.UseShellExecute = $false
$depotStartInfo.CreateNoWindow = $true
$depotStartInfo.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
$depotStartInfo.RedirectStandardOutput = $true
$depotStartInfo.RedirectStandardError = $true
foreach ($depotName in $depotSmokeEnvironment.Keys) { $depotStartInfo.Environment[$depotName] = $depotSmokeEnvironment[$depotName] }
$depotProcess = [Diagnostics.Process]::new()
$depotProcess.StartInfo = $depotStartInfo
try {
    if (-not $depotProcess.Start()) { throw 'Could not start the staged offline smoke.' }
    $depotStdoutTask = $depotProcess.StandardOutput.ReadToEndAsync()
    $depotStderrTask = $depotProcess.StandardError.ReadToEndAsync()
    if (-not $depotProcess.WaitForExit(45000)) { $depotProcess.Kill(); $depotProcess.WaitForExit(); throw 'Staged offline smoke timed out.' }
    $depotStdout = $depotStdoutTask.GetAwaiter().GetResult()
    $depotStderr = $depotStderrTask.GetAwaiter().GetResult()
    [IO.File]::WriteAllText((Join-Path $depotSmokeRoot 'stdout.log'),$depotStdout)
    [IO.File]::WriteAllText((Join-Path $depotSmokeRoot 'stderr.log'),$depotStderr)
    $depotSmokeLog = $depotStdout + $depotStderr
    if ($depotProcess.ExitCode -ne 0 -or $depotSmokeLog -match '(?m)^\s*(?:SCRIPT ERROR:|ERROR: (?!Failed to read the root certificate store\.))') { throw "Offline smoke failed; see $depotSmokeRoot" }
    if ($depotSmokeLog -notmatch 'CHECKOUT_SANDBOX_OFFLINE_PASS:' -or $depotSmokeLog -match 'SDK_VERIFIED|CHECKOUT_REQUESTED|CHECKOUT_START_RESULT') { throw 'Offline smoke did not confirm disabled Steam/checkout.' }
    $depotIsolatedLog = Join-Path $depotSmokeEnvironment.APPDATA 'PetDaDogCheckoutSandbox\checkout-sandbox.log'
    if (-not (Test-Path -LiteralPath $depotIsolatedLog) -or (Get-Content -LiteralPath $depotIsolatedLog -Raw) -notmatch 'CHECKOUT_SANDBOX_OFFLINE_PASS:') { throw 'Offline smoke did not use the isolated profile.' }
}
finally {
    if ($depotProcess.Id -and -not $depotProcess.HasExited) { $depotProcess.Kill(); $depotProcess.WaitForExit() }
    $depotProcess.Dispose()
}

# Recheck original and staged bytes after execution before creating the archive.
$depotStagedFiles = @(Get-ChildItem -LiteralPath $depotContent -Recurse -File -Force)
if ($depotStagedFiles.Count -ne $depotFiles.Count) { throw 'Offline launch changed the staged file count.' }
foreach ($depotFile in $depotFiles) {
    if ((Get-FileHash -LiteralPath $depotFile.Source -Algorithm SHA256).Hash -ne $depotFile.Sha256 -or
        (Get-FileHash -LiteralPath (Join-Path $depotContent $depotFile.Path) -Algorithm SHA256).Hash -ne $depotFile.Sha256) { throw "Source or staged content changed: $($depotFile.Path)" }
}
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory($depotContent,$depotZip,[IO.Compression.CompressionLevel]::Optimal,$false)
$depotArchive = [IO.Compression.ZipFile]::OpenRead($depotZip)
try {
    $depotExpected = @{}
    foreach ($depotFile in $depotFiles) { $depotExpected[$depotFile.Path] = $depotFile }
    $depotEntries = @($depotArchive.Entries | Where-Object { $_.Name })
    if ($depotEntries.Count -ne $depotFiles.Count) { throw 'ZIP entry count differs from the staged content.' }
    foreach ($depotEntry in $depotEntries) {
        $depotEntryName = $depotEntry.FullName.Replace('\','/')
        $depotExpectedFile = $depotExpected[$depotEntryName]
        if (-not $depotExpectedFile -or $depotEntry.Length -ne $depotExpectedFile.Length) { throw "Unexpected ZIP entry: $depotEntryName" }
        $depotEntryStream = $depotEntry.Open()
        $depotSha = [Security.Cryptography.SHA256]::Create()
        try { $depotEntryHash = [BitConverter]::ToString($depotSha.ComputeHash($depotEntryStream)).Replace('-','').ToLowerInvariant() }
        finally { $depotEntryStream.Dispose(); $depotSha.Dispose() }
        if ($depotEntryHash -ne $depotExpectedFile.Sha256) { throw "ZIP content hash mismatch: $depotEntryName" }
        $depotExpected.Remove($depotEntryName)
    }
    if ($depotExpected.Count -ne 0) { throw 'ZIP is missing expected files.' }
}
finally { $depotArchive.Dispose() }
$depotZipInfo = Get-Item -LiteralPath $depotZip
$depotZipHash = (Get-FileHash -LiteralPath $depotZip -Algorithm SHA256).Hash.ToLowerInvariant()
$depotManifest = [ordered]@{ appId=4817200; depotId=4817201; sourcePackage=$depotSource; preparedAtUtc=[DateTime]::UtcNow.ToString('o'); fileCount=$depotFiles.Count; zipBytes=$depotZipInfo.Length; zipSha256=$depotZipHash; offlineSmoke='passed'; files=@($depotFiles | Select-Object Path,Length,Sha256) }
[IO.File]::WriteAllText((Join-Path $depotOutput 'validation.json'),($depotManifest | ConvertTo-Json -Depth 5))
$depotInstructions = @"
Pet that Dog - private Steam checkout sandbox
AppID: 4817200 | DepotID: 4817201
Archive: $($depotZipInfo.Name)
ZIP bytes: $($depotZipInfo.Length) | Files: $($depotFiles.Count)
SHA256: $depotZipHash

This is a DEBUG checkout diagnostic, restricted to the validated developer account.
Offline launch passed with Steam, global input earning and tray disabled in an isolated profile.
It does not belong on the default/public branch. Public Shop cash capability remains off.

1. In Steamworks for app4817200, set Installation > General Installation launch executable
   to PetDaDogCSharp.exe (Windows, no arguments); save and publish app configuration.
2. Confirm depot4817201 is included in the developer account's Dev Comp package.
3. SteamPipe > Web Uploads: select depot4817201 and upload this ZIP as the COMPLETE depot.
   Do not merge with an existing baseline. Use the full replacement option shown by Steam.
   The EXE, PCK and runtime folder are at the ZIP root, with no enclosing content folder.
4. Commit with branch None. In SteamPipe > Builds, create a password-protected test branch
   such as checkout-sandbox and assign this build to that branch. Keep default untouched.
5. In Steam's REAL Pet that Dog entry, Properties > Game Versions & Betas, unlock/select
   the test branch, install it and launch from Steam. The non-Steam shortcut is not this test.
6. Check SDK app4817200, the validated test account, overlay availability and localized price.
   Checkout stays locked until a fresh local short-lived approval file is supplied after live
   priced-schema verification. Do not include that file in any Steam upload.
7. Perform the agreed cancellation/completion checks; checkout-start is not payment success.
   Only confirmed Steam inventory establishes the Golden Bones balance.

Excluded: steam_appid.txt, checkout approval/session files, console launcher, README files,
debug symbols and credential-file patterns. Logs and validation files are beside this ZIP,
not inside the depot. No Steam upload or external configuration change was performed.

Valve references:
https://partner.steamgames.com/doc/sdk/uploading
https://partner.steamgames.com/doc/sdk/updating/partial_depot
https://partner.steamgames.com/doc/store/testing
https://partner.steamgames.com/doc/store/application/branches
"@
[IO.File]::WriteAllText((Join-Path $depotOutput 'upload-instructions.txt'),$depotInstructions)
Write-Output "STEAM_DEPOT_READY: $depotZip"
Write-Output "App4817200 / depot4817201; $($depotFiles.Count) files; $($depotZipInfo.Length) bytes; SHA256 $depotZipHash"
Write-Output 'Renamed offline export passed; original source, staged content and every ZIP entry have matching SHA256 hashes.'
