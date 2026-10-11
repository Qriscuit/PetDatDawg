# Build the Windows client

Double-click `Build-Windows.bat` in the repository root. The window stays open
to show success or the error. A successful build replaces `Builds` with the
complete client package; launch `Builds/PetDaDogCSharp.exe` with Steam running.
Keep the executable, PCK, and runtime folder together.

The build uses Godot **4.6.3 Mono**, its matching Windows x64 export templates,
and the **.NET 8 SDK**. On this machine it uses the known-good runtime on `W:`
and templates in `Pet_Da_Dog_CSharp/.godot/build-tools/templates-4.6.3`. Installed
Godot templates in `%APPDATA%/Godot/export_templates/4.6.3.stable.mono` also work.
For another machine, pass `-GodotPath` and `-TemplatesDirectory` to the batch
file or set `PDD_GODOT_PATH` and `PDD_GODOT_TEMPLATES`.

The public Worker URL comes from `pdd/backend_url` in `project.godot`; the
package requires an HTTPS address. The public Steam AppID comes from
`steam_appid.txt` (default `4817200`). The sample AppID `480` is rejected for
packaged builds. `PDD_BACKEND_URL` still overrides the Worker address at runtime.
No backend deployment or Steam publisher key is needed to build the client.
Local environment and credential files are excluded from staging.

Each build compiles Release code, imports resources, exports into an isolated
staging folder, checks the managed/native runtime files, and launches the
package briefly with Steam and tray disabled. This launch smoke does not grant
Pets. Only after those checks pass does the new package replace `Builds`.
`Builds/Build-info.txt` records its public configuration and validation result;
logs and the previous package are kept under `.godot/windows-export`.

For command-line use:

```powershell
.\scripts\Export-Windows.ps1
# Validate a staged package without replacing Builds:
.\scripts\Export-Windows.ps1 -StageOnly
```

Set `PDD_BUILD_NO_PAUSE=1` when calling the batch file from automation.
