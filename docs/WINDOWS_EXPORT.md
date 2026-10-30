# Windows exports

Build the entire Windows package after changing C# code or scenes. Updating only the `.pck` leaves old C# assemblies and the old Godot runtime in `Builds`, which can make menu commands fail.

Use Godot 4.6.3 Mono and its matching [official export templates](https://github.com/godotengine/godot-builds/releases/tag/4.6.3-stable). The script discovers a complete extracted Mono runtime under the repository's `tools` folder or client's `.godot/build-tools` cache, then checks `PATH`. See [the portable setup](WINDOWS_BUILD.md) for the expected folders. Install the Mono templates through Godot, or extract their `templates` directory into `tools/templates-4.6.3`:

```powershell
./scripts/Export-Windows.ps1
# Optional explicit locations, relative to the repository root:
./scripts/Export-Windows.ps1 -GodotPath 'tools/Godot_v4.6.3-stable_mono_win64/Godot_v4.6.3-stable_mono_win64_console.exe' -TemplatesDirectory 'tools/templates-4.6.3'
```

The script snapshots the client under `Pet_Da_Dog_CSharp/.godot/windows-export`, builds only the client project, imports resources, and exports a complete self-contained Windows x64 release. It checks the native executable, resource pack, C# assemblies, .NET runtime, and Steam library, then smoke-tests the actual exported entrypoint and opens Status using `-- --status`, with Steam and the tray disabled and an isolated settings directory. Only a successful export replaces `Builds`; the previous package is retained beside the export logs. Close the running exported game first so Windows can move the old package.

Use `-StageOnly` to validate a package without replacing `Builds`. Keep the generated executable, `.pck`, console executable, and `data_PetDaDogCSharp_windows_x86_64` directory together when distributing or updating the game. Never copy backend configuration or publisher keys into the client package.

## Prepare the Steam depot

After the full Windows export passes, prepare the ordinary game package with
PowerShell 7.2 or newer:

```powershell
pwsh -File ./scripts/Prepare-SteamDepot.ps1
```

This reads the complete `Builds` folder and creates a separate, uniquely named
upload package under `Pet_Da_Dog_CSharp/.godot/steam-release/`. It checks the
Release assembly and packaged backend/AppID, then verifies every copied file
and ZIP entry with SHA-256. The depot contains `PetDaDogCSharp.exe`, its matching
`.pck`, and the complete runtime directory at the archive root. Development
AppID files, console launchers, debug symbols, diagnostics and credential files
are excluded. Valve requires omitting `steam_appid.txt` from the Steam depot;
Steam supplies the AppID when the game is launched from its library.
[Steamworks initialization guidance](https://partner.steamgames.com/doc/sdk/api#initialization-and-shutdown)

The normal local `Builds` folder retains its public development AppID file for
direct executable testing. Keep the upload archive separate from that folder.
Preparing it does not upload a build, publish a Steam branch, or replace an
installed game. Use the generated instructions and validation manifest for the
explicitly requested Steam publication step. The checkout sandbox's separate
packager is for its diagnostic build and must not replace this production path.

The October 30 export targets `https://petdadawg.petthatdog.workers.dev` and
AppID `4817200`, includes 30-second earning batches, and explains automatic
Steam reconnection in the menu. Its complete exported Release entrypoint
passed the offline launch smoke and an isolated headless Steam-connected
startup check: authentication and inventory sync returned 450 confirmed Pets,
with no pending grants and desktop input earning disabled. That check does not
verify the Steam overlay or an installed/published Steam build.
