#requires -Version 7.2
[CmdletBinding()]
param(
    [string]$PackagePath,
    [string]$ExpectedBackendUrl = 'https://petdadawg.petthatdog.workers.dev'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$depotRepo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..')).TrimEnd('\', '/')
$depotCache = Join-Path $depotRepo 'Pet_Da_Dog_CSharp/.godot/steam-release'
if (-not $PackagePath) { $PackagePath = Join-Path $depotRepo 'Builds' }
if (-not [IO.Path]::IsPathRooted($PackagePath)) { $PackagePath = Join-Path $depotRepo $PackagePath }
$depotSource = [IO.Path]::GetFullPath($PackagePath).TrimEnd('\', '/')
$depotRuntimeName = 'data_PetDaDogCSharp_windows_x86_64'
$depotRuntimeSource = Join-Path $depotSource $depotRuntimeName
$depotOutput = Join-Path $depotCache ([DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
$depotContent = Join-Path $depotOutput 'content'
$depotZip = Join-Path $depotOutput 'PetDaDog-4817200-depot-4817201-windows-release.zip'

function Assert-DepotChild([string]$Candidate, [string]$Parent) {
    $fullPath = [IO.Path]::GetFullPath($Candidate)
    $parentPath = [IO.Path]::GetFullPath($Parent).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    if (-not $fullPath.StartsWith($parentPath, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Depot path escaped its intended directory: $fullPath"
    }
    return $fullPath
}

function Assert-DepotNoReparsePoint([string]$Candidate) {
    $item = Get-Item -LiteralPath $Candidate -Force
    while ($null -ne $item) {
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Reparse point refused: $($item.FullName)" }
        $item = if ($item -is [IO.DirectoryInfo]) { $item.Parent } else { $item.Directory }
    }
}

function Test-DepotExcluded([IO.FileSystemInfo]$Item) {
    if ($Item -is [IO.DirectoryInfo]) { return $Item.Name -match '(?i)^(?:tests?|\.git|\.godot|obj|bin)$' }
    if ($Item.Name -match '(?i)^(?:steam_appid\.txt|Build-info\.txt|README(?:\..*)?|checkout-(?:approval|session)\.json|\.env(?:\..*)?|\.dev\.vars(?:\..*)?|export_credentials\.cfg)$') { return $true }
    if ($Item.Extension -in @('.pdb', '.pem', '.key', '.pfx', '.p12', '.log', '.vdf', '.md', '.cs', '.csproj', '.tscn', '.ps1', '.bat', '.cmd')) { return $true }
    if ($Item.Name -match '(?i)\.console\.exe$|(?:^|[._-])tests?(?:[._-]|$)|^PetDaDog\.Backend\.') { return $true }
    if ($Item.Extension -notin @('.dll', '.exe') -and $Item.Name -match '(?i)(?:credential|secret|token)') { return $true }
    return $false
}

function Get-DepotManagedProvenance([string]$AssemblyPath) {
    # Read ECMA metadata only. Do not load or execute the exported game assembly.
    Add-Type -AssemblyName System.Reflection.Metadata
    $stream = [IO.File]::OpenRead($AssemblyPath)
    $pe = $null
    try {
        $pe = [System.Reflection.PortableExecutable.PEReader]::new($stream)
        if (-not $pe.HasMetadata) { throw 'The game assembly has no managed metadata.' }
        $reader = [System.Reflection.Metadata.PEReaderExtensions]::GetMetadataReader($pe)
        $assembly = $reader.GetAssemblyDefinition()
        if ($reader.GetString($assembly.Name) -ne 'PetDaDogCSharp') { throw 'The package contains an unexpected managed game assembly.' }
        $configuration = $null
        $debugModes = 0
        foreach ($handle in $assembly.GetCustomAttributes()) {
            $attribute = $reader.GetCustomAttribute($handle)
            if ($attribute.Constructor.Kind -ne [System.Reflection.Metadata.HandleKind]::MemberReference) { continue }
            $member = $reader.GetMemberReference([System.Reflection.Metadata.MemberReferenceHandle]$attribute.Constructor)
            if ($member.Parent.Kind -ne [System.Reflection.Metadata.HandleKind]::TypeReference) { continue }
            $type = $reader.GetTypeReference([System.Reflection.Metadata.TypeReferenceHandle]$member.Parent)
            $name = $reader.GetString($type.Name)
            $namespace = $reader.GetString($type.Namespace)
            if (($namespace -ne 'System.Reflection' -or $name -ne 'AssemblyConfigurationAttribute') -and
                ($namespace -ne 'System.Diagnostics' -or $name -ne 'DebuggableAttribute')) { continue }
            $blob = $reader.GetBlobReader($attribute.Value)
            if ($blob.ReadUInt16() -ne 1) { throw 'The game assembly has invalid build attributes.' }
            if ($name -eq 'AssemblyConfigurationAttribute') { $configuration = $blob.ReadSerializedString() }
            else { $debugModes = $blob.ReadInt32() }
        }
        if ($configuration -notin @('Release', 'ExportRelease') -or ($debugModes -band 256)) {
            throw "A Release game assembly is required; found '$configuration' with debug flags $debugModes."
        }
        $backend = $null
        $appId = $null
        foreach ($typeHandle in $reader.TypeDefinitions) {
            $type = $reader.GetTypeDefinition($typeHandle)
            $typeName = $reader.GetString($type.Name)
            if ($typeName -notin @('BackendPetClient', 'SteamAppId')) { continue }
            foreach ($fieldHandle in $type.GetFields()) {
                $field = $reader.GetFieldDefinition($fieldHandle)
                $fieldName = $reader.GetString($field.Name)
                if (($typeName -ne 'BackendPetClient' -or $fieldName -ne 'DefaultBackendUrl') -and
                    ($typeName -ne 'SteamAppId' -or $fieldName -ne 'GameAppId')) { continue }
                $valueHandle = $field.GetDefaultValue()
                if ($valueHandle.IsNil) { throw 'A required public client configuration constant is missing.' }
                $constant = $reader.GetConstant($valueHandle)
                $bytes = $reader.GetBlobBytes($constant.Value)
                if ($fieldName -eq 'DefaultBackendUrl') {
                    if ($constant.TypeCode -ne [System.Reflection.Metadata.ConstantTypeCode]::String) { throw 'The client backend constant has an unexpected type.' }
                    $backend = [Text.Encoding]::Unicode.GetString($bytes)
                } else {
                    if ($constant.TypeCode -ne [System.Reflection.Metadata.ConstantTypeCode]::UInt32 -or $bytes.Length -ne 4) { throw 'The client AppID constant has an unexpected type.' }
                    $appId = [BitConverter]::ToUInt32($bytes, 0)
                }
            }
        }
        return [pscustomobject]@{ Configuration = $configuration; DebugModes = $debugModes; BackendUrl = $backend; AppId = $appId }
    } finally {
        if ($null -ne $pe) { $pe.Dispose() }
        $stream.Dispose()
    }
}

$null = Assert-DepotChild $depotSource $depotRepo
$null = Assert-DepotChild $depotOutput $depotCache
Assert-DepotNoReparsePoint $depotSource
$backendUri = $null
if (-not [Uri]::TryCreate($ExpectedBackendUrl, [UriKind]::Absolute, [ref]$backendUri) -or
    $backendUri.Scheme -ne 'https' -or $backendUri.UserInfo -or $backendUri.Query -or $backendUri.Fragment -or $backendUri.AbsolutePath -ne '/') {
    throw 'ExpectedBackendUrl must be the public HTTPS Worker origin.'
}
$expectedBackend = $backendUri.AbsoluteUri.TrimEnd('/')
$depotRequired = @('PetDaDogCSharp.exe', 'PetDaDogCSharp.pck', 'Build-info.txt',
    "$depotRuntimeName/PetDaDogCSharp.dll", "$depotRuntimeName/PetDaDogCSharp.deps.json", "$depotRuntimeName/PetDaDogCSharp.runtimeconfig.json",
    "$depotRuntimeName/GodotSharp.dll", "$depotRuntimeName/Steamworks.NET.dll", "$depotRuntimeName/steam_api64.dll",
    "$depotRuntimeName/coreclr.dll", "$depotRuntimeName/hostfxr.dll", "$depotRuntimeName/hostpolicy.dll")
foreach ($required in $depotRequired) {
    $source = Assert-DepotChild (Join-Path $depotSource $required) $depotSource
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw "The complete validated Windows package is required; missing $required" }
    Assert-DepotNoReparsePoint $source
}

$buildInfoPath = Join-Path $depotSource 'Build-info.txt'
$buildInfoSha256 = (Get-FileHash -LiteralPath $buildInfoPath -Algorithm SHA256).Hash.ToLowerInvariant()
$managedSha256 = (Get-FileHash -LiteralPath (Join-Path $depotRuntimeSource 'PetDaDogCSharp.dll') -Algorithm SHA256).Hash.ToLowerInvariant()
$packSha256 = (Get-FileHash -LiteralPath (Join-Path $depotSource 'PetDaDogCSharp.pck') -Algorithm SHA256).Hash.ToLowerInvariant()
$buildInfo = [IO.File]::ReadAllText($buildInfoPath)
if ($buildInfo -notmatch '(?m)^Pet Da Dog Windows build\r?$' -or
    $buildInfo -notmatch '(?m)^Godot: 4\.6\.3\.stable\.mono\.[^\r\n]+\r?$' -or
    $buildInfo -notmatch '(?m)^Steam AppID: 4817200\r?$' -or
    $buildInfo -notmatch '(?m)^Validation: Release build, resource import, complete export, Steam-disabled launch smoke passed\.\r?$') {
    throw 'Build-info.txt does not attest a complete validated Godot 4.6.3 Mono Release export for AppID 4817200.'
}
$buildWorker = [regex]::Match($buildInfo, '(?m)^Worker: ([^\r\n]+)\r?$')
if (-not $buildWorker.Success -or $buildWorker.Groups[1].Value.TrimEnd('/') -ne $expectedBackend) { throw 'Build-info.txt points to a different backend. Export the updated client before preparing its depot.' }
$buildTimestamp = [regex]::Match($buildInfo, '(?m)^Built at \(UTC\): ([^\r\n]+)\r?$')
$parsedBuildTime = [DateTime]::MinValue
if (-not $buildTimestamp.Success -or -not [DateTime]::TryParseExact($buildTimestamp.Groups[1].Value, 'yyyy-MM-dd HH:mm:ss', [Globalization.CultureInfo]::InvariantCulture,
    [Globalization.DateTimeStyles]::AssumeUniversal -bor [Globalization.DateTimeStyles]::AdjustToUniversal, [ref]$parsedBuildTime)) { throw 'Build-info.txt has no valid UTC export timestamp.' }

$provenance = Get-DepotManagedProvenance (Join-Path $depotRuntimeSource 'PetDaDogCSharp.dll')
if ($provenance.BackendUrl -ne $expectedBackend -or $provenance.AppId -ne 4817200) { throw 'The actual exported assembly does not match the expected public Worker and Steam AppID.' }
$pckBytes = [IO.File]::ReadAllBytes((Join-Path $depotSource 'PetDaDogCSharp.pck'))
if ($pckBytes.Length -lt 24 -or [BitConverter]::ToUInt32($pckBytes, 0) -ne 0x43504447 -or
    [BitConverter]::ToUInt32($pckBytes, 8) -ne 4 -or [BitConverter]::ToUInt32($pckBytes, 12) -ne 6 -or [BitConverter]::ToUInt32($pckBytes, 16) -ne 3) {
    throw 'The game resource pack is not a matching Godot 4.6.3 PCK.'
}
if (-not [Text.Encoding]::UTF8.GetString($pckBytes).Contains($expectedBackend)) { throw 'The exported resource pack does not contain the expected public Worker configuration.' }
$pckBytes = $null

$depotFiles = [Collections.Generic.List[object]]::new()
$depotNames = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
function Add-DepotFile([string]$Source, [string]$RelativePath) {
    $sourceFull = Assert-DepotChild $Source $depotSource
    Assert-DepotNoReparsePoint $sourceFull
    $relative = $RelativePath.Replace('\', '/')
    if ([IO.Path]::IsPathRooted($relative) -or $relative.Contains(':') -or $relative.Split('/') -contains '..' -or $relative.Split('/') -contains '.' -or $relative.Split('/') -contains '') { throw "Unsafe depot entry: $relative" }
    if (-not $depotNames.Add($relative)) { throw "Duplicate depot entry: $relative" }
    $item = Get-Item -LiteralPath $sourceFull
    $depotFiles.Add([pscustomobject]@{ Source = $sourceFull; Path = $relative; Length = $item.Length; Sha256 = (Get-FileHash -LiteralPath $sourceFull -Algorithm SHA256).Hash.ToLowerInvariant() })
}
function Add-DepotRuntime([string]$Source, [string]$RelativePath) {
    Assert-DepotNoReparsePoint $Source
    foreach ($item in Get-ChildItem -LiteralPath $Source -Force | Sort-Object Name) {
        # Reject links even if their names would otherwise be excluded.
        Assert-DepotNoReparsePoint $item.FullName
        if (Test-DepotExcluded $item) { continue }
        $relative = "$RelativePath/$($item.Name)"
        if ($item -is [IO.DirectoryInfo]) { Add-DepotRuntime $item.FullName $relative }
        else { Add-DepotFile $item.FullName $relative }
    }
}
Add-DepotFile (Join-Path $depotSource 'PetDaDogCSharp.exe') 'PetDaDogCSharp.exe'
Add-DepotFile (Join-Path $depotSource 'PetDaDogCSharp.pck') 'PetDaDogCSharp.pck'
Add-DepotRuntime $depotRuntimeSource $depotRuntimeName
foreach ($required in $depotRequired | Where-Object { $_ -ne 'Build-info.txt' }) {
    if (-not $depotNames.Contains($required.Replace('\', '/'))) { throw "A required runtime file was excluded: $required" }
}
$selectedManaged = $depotFiles | Where-Object { $_.Path -eq "$depotRuntimeName/PetDaDogCSharp.dll" } | Select-Object -First 1
$selectedPack = $depotFiles | Where-Object { $_.Path -eq 'PetDaDogCSharp.pck' } | Select-Object -First 1
if ($selectedManaged.Sha256 -ne $managedSha256 -or $selectedPack.Sha256 -ne $packSha256 -or
    (Get-FileHash -LiteralPath $buildInfoPath -Algorithm SHA256).Hash.ToLowerInvariant() -ne $buildInfoSha256) {
    throw 'The package configuration changed during provenance validation.'
}

# All validation above is read-only. Every generated file stays under this new run.
$existingOutputParent = $depotCache
while (-not (Test-Path -LiteralPath $existingOutputParent)) { $existingOutputParent = [IO.Path]::GetDirectoryName($existingOutputParent) }
Assert-DepotNoReparsePoint $existingOutputParent
New-Item -ItemType Directory -Path $depotContent -Force | Out-Null
Assert-DepotNoReparsePoint $depotOutput
foreach ($file in $depotFiles) {
    $destination = Assert-DepotChild (Join-Path $depotContent $file.Path) $depotContent
    New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($destination)) -Force | Out-Null
    Copy-Item -LiteralPath $file.Source -Destination $destination
    if ((Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash.ToLowerInvariant() -ne $file.Sha256) { throw "Staging hash mismatch: $($file.Path)" }
}

[IO.Compression.ZipFile]::CreateFromDirectory($depotContent, $depotZip, [IO.Compression.CompressionLevel]::Optimal, $false)
$archive = [IO.Compression.ZipFile]::OpenRead($depotZip)
try {
    $expected = @{}
    foreach ($file in $depotFiles) { $expected[$file.Path] = $file }
    $entries = @($archive.Entries | Where-Object { $_.Name })
    if ($entries.Count -ne $depotFiles.Count) { throw 'ZIP entry count differs from the staged content.' }
    foreach ($entry in $entries) {
        $name = $entry.FullName.Replace('\', '/')
        $file = $expected[$name]
        if (-not $file -or $entry.Length -ne $file.Length) { throw "Unexpected ZIP entry: $name" }
        $entryStream = $entry.Open()
        $sha = [Security.Cryptography.SHA256]::Create()
        try { $entryHash = [Convert]::ToHexString($sha.ComputeHash($entryStream)).ToLowerInvariant() }
        finally { $sha.Dispose(); $entryStream.Dispose() }
        if ($entryHash -ne $file.Sha256) { throw "ZIP content hash mismatch: $name" }
        $expected.Remove($name)
    }
    if ($expected.Count) { throw 'ZIP is missing expected files.' }
} finally { $archive.Dispose() }

$stagedFiles = @(Get-ChildItem -LiteralPath $depotContent -Recurse -File -Force)
if ($stagedFiles.Count -ne $depotFiles.Count) { throw 'Staged content changed during archive preparation.' }
foreach ($file in $depotFiles) {
    if ((Get-FileHash -LiteralPath $file.Source -Algorithm SHA256).Hash.ToLowerInvariant() -ne $file.Sha256 -or
        (Get-FileHash -LiteralPath (Join-Path $depotContent $file.Path) -Algorithm SHA256).Hash.ToLowerInvariant() -ne $file.Sha256) { throw "Source or staged bytes changed: $($file.Path)" }
}
if ((Get-FileHash -LiteralPath $buildInfoPath -Algorithm SHA256).Hash.ToLowerInvariant() -ne $buildInfoSha256) { throw 'Build-info.txt changed during archive preparation.' }
$buildInfoSnapshot = Join-Path $depotOutput 'source-Build-info.txt'
Copy-Item -LiteralPath $buildInfoPath -Destination $buildInfoSnapshot
if ((Get-FileHash -LiteralPath $buildInfoSnapshot -Algorithm SHA256).Hash.ToLowerInvariant() -ne $buildInfoSha256) { throw 'Build-info.txt snapshot differs from the validated export.' }
$zipInfo = Get-Item -LiteralPath $depotZip
$zipHash = (Get-FileHash -LiteralPath $depotZip -Algorithm SHA256).Hash.ToLowerInvariant()
$manifest = [ordered]@{
    appId = 4817200; depotId = 4817201; preparedAtUtc = [DateTime]::UtcNow.ToString('o'); sourcePackage = $depotSource
    builtAtUtc = $parsedBuildTime.ToString('o'); backendUrl = $backendUri.AbsoluteUri; godotVersion = '4.6.3.stable.mono'
    assemblyConfiguration = $provenance.Configuration; assemblyDebugModes = $provenance.DebugModes; sourceExportSmoke = 'passed'
    gameExecutedByPackager = $false; steamCalls = 0; uploaded = $false
    buildInfoSnapshot = 'source-Build-info.txt'
    buildInfoSha256 = $buildInfoSha256
    fileCount = $depotFiles.Count; zipBytes = $zipInfo.Length; zipSha256 = $zipHash
    files = @($depotFiles | Select-Object Path, Length, Sha256)
}
[IO.File]::WriteAllText((Join-Path $depotOutput 'validation.json'), ($manifest | ConvertTo-Json -Depth 6) + [Environment]::NewLine)
$instructions = @"
Pet Da Dog - Windows Release depot
AppID: 4817200 | DepotID: 4817201
Archive: $($zipInfo.Name)
Backend: $($backendUri.AbsoluteUri)
Files: $($depotFiles.Count) | ZIP bytes: $($zipInfo.Length)
SHA256: $zipHash

This is the complete ordinary Release game, prepared locally from a validated
Godot 4.6.3 Mono export. The packager did not launch the game, contact Steam,
upload a build, change an installed copy, or publish a branch.

The ZIP root contains PetDaDogCSharp.exe, PetDaDogCSharp.pck and the complete
data_PetDaDogCSharp_windows_x86_64 runtime directory. Keep them together.
Validation and instructions are outside the depot ZIP.

When publishing is separately authorized:
1. Confirm the Windows launch executable for AppID 4817200 is PetDaDogCSharp.exe,
   with no diagnostic arguments, and depot 4817201 is included in the test package.
2. Upload this ZIP as the complete replacement content for depot 4817201 using
   SteamPipe. Do not merge a new PCK with older executable or runtime files.
3. Assign the resulting build to a private test branch first. Launch the real
   Steam app from that branch and verify sign-in, confirmed inventory and Shop.
4. Promote the tested build only after the desired branch publication is approved.

The live service remains golden-bones-v1. This package does not publish the
locally staged v2 Steam economy or enable cash/Market functionality.

Excluded: steam_appid.txt, console launcher, debug symbols, README/build-info,
logs, scripts, loose test files and credential-file patterns. Publisher keys
remain on the backend. Per-file and ZIP hashes are in validation.json.

SteamPipe reference: https://partner.steamgames.com/doc/sdk/uploading
Branches reference: https://partner.steamgames.com/doc/store/application/branches
"@
[IO.File]::WriteAllText((Join-Path $depotOutput 'upload-instructions.txt'), $instructions)
[IO.File]::WriteAllText((Join-Path $depotCache 'latest-package.txt'), $depotOutput + [Environment]::NewLine)
Write-Output "STEAM_DEPOT_READY: $depotZip"
Write-Output "App 4817200 / depot 4817201; $($depotFiles.Count) files; $($zipInfo.Length) bytes; SHA256 $zipHash"
Write-Output 'Release assembly/public configuration verified; every source, staged file and ZIP entry has matching SHA256 hashes.'
Write-Output 'Prepared locally only. No game launch, Steam calls, upload, branch publication or installed-copy change.'
