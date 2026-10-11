# Build the Windows client

Double-click `Build-Windows.bat` in the repository root. The window stays open
to show success or the error. A successful build replaces `Builds` with the
complete client package; launch `Builds/PetDaDogCSharp.exe` with Steam running.
Keep the executable, PCK, and runtime folder together.

The build uses Godot **4.6.3 Mono**, its matching Windows x64 Mono export
templates, and the **.NET 8 SDK**. Install the SDK on each development machine.
Repository paths are relative to the checkout; no particular drive or username
is required, and the batch file can be launched from another working directory.

For a portable setup, extract the complete official Godot Windows x64 Mono
download into `tools/Godot_v4.6.3-stable_mono_win64` at the repository root. Keep
both executables and the `GodotSharp` folder together. Extract the Mono export
templates into `tools/templates-4.6.3`, with `version.txt` directly inside that
folder. These tool folders are ignored by Git; each developer supplies them.

The script searches `tools`, then the client's `.godot/build-tools` cache, then
Godot applications on `PATH`, and verifies Godot is 4.6.3 Mono. Templates can
also come from `Pet_Da_Dog_CSharp/.godot/build-tools/templates-4.6.3` or the
normal installation in `%APPDATA%/Godot/export_templates/4.6.3.stable.mono`.
For other locations, pass `-GodotPath` and `-TemplatesDirectory` to the batch
file or set `PDD_GODOT_PATH` and `PDD_GODOT_TEMPLATES`. Relative overrides are
resolved from the repository root, including when launched from another folder.

The public Worker URL comes from `pdd/backend_url` in `project.godot`; the
package requires an HTTPS address. The public Steam AppID comes from
`steam_appid.txt` (default `4817200`). The sample AppID `480` is rejected for
packaged builds. `PDD_BACKEND_URL` still overrides the Worker address at runtime.
No backend deployment or Steam publisher key is needed to build the client.
Local environment and credential files are excluded from staging.

The client also defaults to the shared Worker when `pdd/backend_url` is missing.
If Status instead shows a connection to `127.0.0.1:5155`, update the checkout and
rebuild the entire client. Remove any old localhost `PDD_BACKEND_URL` override
from the environment used to launch Godot or the game, then restart it. Local
backend development remains available by setting that override explicitly.

Each build compiles Release code, imports resources, exports into an isolated
staging folder, checks the managed/native runtime files, and launches the
package briefly with Steam and tray disabled. This launch smoke does not grant
Pets. Only after those checks pass does the new package replace `Builds`.
`Builds/Build-info.txt` records its public configuration and validation result;
logs and the previous package are kept under `.godot/windows-export`.

For command-line use:

```powershell
.\scripts\Export-Windows.ps1
# Explicit portable locations (resolved from the repository root):
.\Build-Windows.bat -GodotPath 'tools/Godot_v4.6.3-stable_mono_win64/Godot_v4.6.3-stable_mono_win64_console.exe' -TemplatesDirectory 'tools/templates-4.6.3'
# Validate a staged package without replacing Builds:
.\scripts\Export-Windows.ps1 -StageOnly
```

Set `PDD_BUILD_NO_PAUSE=1` when calling the batch file from automation.
