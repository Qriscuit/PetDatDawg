# Windows exports

Build the entire Windows package after changing C# code or scenes. Updating only the `.pck` leaves old C# assemblies and the old Godot runtime in `Builds`, which can make menu commands fail.

Use Godot 4.6.3 Mono and its matching [official export templates](https://github.com/godotengine/godot-builds/releases/tag/4.6.3-stable). Install the Mono templates through Godot, or extract their `templates` directory and pass its path:

```powershell
./scripts/Export-Windows.ps1
# Optional alternate locations:
./scripts/Export-Windows.ps1 -GodotPath 'W:/Godot_v4.6.3-stable_mono_win64/Godot_v4.6.3-stable_mono_win64_console.exe' -TemplatesDirectory 'C:/path/to/templates'
```

The script snapshots the client under `Pet_Da_Dog_CSharp/.godot/windows-export`, builds only the client project, imports resources, and exports a complete self-contained Windows x64 release. It checks the native executable, resource pack, C# assemblies, .NET runtime, and Steam library, then smoke-tests the actual exported entrypoint and opens Status using `-- --status`, with Steam and the tray disabled and an isolated settings directory. Only a successful export replaces `Builds`; the previous package is retained beside the export logs. Close the running exported game first so Windows can move the old package.

Use `-StageOnly` to validate a package without replacing `Builds`. Keep the generated executable, `.pck`, console executable, and `data_PetDaDogCSharp_windows_x86_64` directory together when distributing or updating the game. Never copy backend configuration or publisher keys into the client package.
