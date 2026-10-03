# Pet Da Dog — Unity Script Staging Area

This folder is intentionally a source-only Unity package. Copy its contents into
`Assets/PetDaDog/` in the new Unity project; it does not contain a Unity project,
scenes, imported assets, or generated `.meta` files.

## Target configuration

- Windows Standalone, x86_64, IL2CPP.
- Add Facepunch.Steamworks **2.5.2**, then add
  `PDD_FACEPUNCH_STEAMWORKS` to **Player Settings → Scripting Define Symbols**.
  Without that define the client deliberately runs with Steam disabled, allowing
  overlay and UI work to be tested before the Steam package is installed.
- Put `steam_api64.dll` beside the built player as required by Facepunch.Steamworks.
- Use a 2D scene with a Camera, a `FootAnchor` transform, a `VisualRoot` child,
  and a `SpriteRenderer` child assigned to `DesktopPetController.DogSprite`.
  Assign `Doggo.png` as a readable, un-atlased sprite and assign a heart prefab
  containing `FloatingHeartBehaviour`.
- Set the Camera to Orthographic. `DesktopPetController` configures it as a
  pixel-space camera at runtime. Its clear colour is the chroma-key colour used
  by the Windows overlay bridge; do not use that exact colour in pet artwork.
- Start the player with `-popupwindow -screen-fullscreen 0`. The bridge applies
  the final borderless, transparent, no-activate window styles after the player
  creates its HWND.

## Root object

Create one persistent `PetDaDog` GameObject with these components:

- `PetDaDogBootstrap`
- `PetSettingsStore`
- `SteamIntegrationBehaviour`
- `BackendPetClientBehaviour`
- `WindowsOverlayBridge`
- `NativeStatusWindow`
- `DesktopPetController`

Wire the serialized component references in `PetDaDogBootstrap`. The bootstrap
connects native click/tray events to the dog and status window. The existing
ASP.NET backend remains unchanged; publisher keys must remain backend-only.

## Development switches

- `PDD_BACKEND_URL` defaults to `http://127.0.0.1:5155`.
- `PDD_STEAM_APP_ID` overrides `steam_appid.txt`; development falls back to `480`.
- `PDD_DISABLE_STEAM=1` disables Steam initialization.
- `PDD_DISABLE_TRAY=1` disables the tray icon and global hotkey.

`PetSettingsStore` imports the existing Godot configuration once, if present at
`%APPDATA%/Godot/app_userdata/PetDaDogCSharp/pet_settings.cfg`, then persists
only the five local visual/input preferences in Unity's persistent-data folder.
It never saves pets, grant IDs, backend tokens, or Steam credentials.

