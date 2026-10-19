param(
    [string]$GodotPath = 'W:\Godot_v4.6.3-stable_mono_win64\Godot_v4.6.3-stable_mono_win64_console.exe',
    [switch]$SkipBuild
)
$ErrorActionPreference = 'Stop'
$shortcutProject = Split-Path -Parent $PSScriptRoot
$shortcutVariables = @('PDD_DISABLE_STEAM','PDD_DISABLE_GLOBAL_INPUT','PDD_DISABLE_TRAY','APPDATA','LOCALAPPDATA','TEMP','TMP')
$shortcutPrevious = @{}
foreach ($shortcutName in $shortcutVariables) { $shortcutPrevious[$shortcutName] = [Environment]::GetEnvironmentVariable($shortcutName,'Process') }
try {
    $env:PDD_DISABLE_STEAM = '1'
    $env:PDD_DISABLE_GLOBAL_INPUT = '1'
    $env:PDD_DISABLE_TRAY = '0'
    $shortcutProfile = Join-Path $shortcutProject ('.godot/menu-hotkey-smoke-' + [Guid]::NewGuid().ToString('N'))
    $env:APPDATA = Join-Path $shortcutProfile 'Roaming'
    $env:LOCALAPPDATA = Join-Path $shortcutProfile 'Local'
    $env:TEMP = Join-Path $shortcutProfile 'Temp'
    $env:TMP = $env:TEMP
    New-Item -ItemType Directory -Force -Path $env:APPDATA,$env:LOCALAPPDATA,$env:TEMP | Out-Null
    if (-not $SkipBuild) {
        dotnet build (Join-Path $shortcutProject 'PetDaDogCSharp.csproj')
        if ($LASTEXITCODE -ne 0) { throw 'The menu shortcut smoke build failed.' }
    }
    Write-Output 'Checking native shortcut registration and own-window messages. Steam and physical input earning are disabled.'
    $shortcutProcess = Start-Process -FilePath $GodotPath -ArgumentList @('--headless','--path',('"' + $shortcutProject + '"'),'--scene','res://Tests/NativeMenuHotkeySmokeTest.tscn','--no-header') -WorkingDirectory $shortcutProject -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $shortcutProfile 'stdout.log') -RedirectStandardError (Join-Path $shortcutProfile 'stderr.log')
    $null = $shortcutProcess.Handle
    if (-not $shortcutProcess.WaitForExit(60000)) { Stop-Process -Id $shortcutProcess.Id -Force; throw 'The native shortcut smoke timed out.' }
    $shortcutProcess.WaitForExit()
    $shortcutOutput = @(Get-Content (Join-Path $shortcutProfile 'stdout.log'); Get-Content (Join-Path $shortcutProfile 'stderr.log'))
    $shortcutOutput | Write-Output
    if ($shortcutProcess.ExitCode -ne 0 -or -not ($shortcutOutput -match '^NATIVE_MENU_HOTKEY_SMOKE_PASS:')) { throw "Native shortcut smoke failed. Logs: $shortcutProfile" }
}
finally {
    foreach ($shortcutName in $shortcutVariables) { [Environment]::SetEnvironmentVariable($shortcutName,$shortcutPrevious[$shortcutName],'Process') }
}
